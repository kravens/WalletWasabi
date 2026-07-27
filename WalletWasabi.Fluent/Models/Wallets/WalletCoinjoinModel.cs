using NBitcoin;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Hwi;
using WalletWasabi.Hwi.Coldcard;
using WalletWasabi.Hwi.Trezor;
using WalletWasabi.Logging;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.WabiSabi.Client.CoinJoin.Manager;
using WalletWasabi.WabiSabi.Client.CoinJoinProgressEvents;
using WalletWasabi.WabiSabi.Client.StatusChangedEvents;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.Models.Wallets;

public enum TrezorAuthorizationStatus
{
	Idle,
	AwaitingConfirmation,
	Confirmed,
	BridgeNotFound,
	DeviceNotFound,
	Failed,
}

[AppLifetime]
public partial class WalletCoinjoinModel : ReactiveObject
{
	private readonly IServices _services;
	private readonly Wallet _wallet;
	private readonly WalletSettingsModel _settings;
	private CoinJoinManager _coinJoinManager;
	[AutoNotify] private bool _isCoinjoining;
	[AutoNotify] private TrezorAuthorizationStatus _trezorAuthorization = TrezorAuthorizationStatus.Idle;

	/// <summary>Why the last authorization failed, when the device told us something worth acting on.
	/// A Coldcard reports things the user has to go and fix — HSM commands switched off, the wrong device
	/// plugged in, firmware too old — and "press Play to retry" is a dead end for all of them.</summary>
	[AutoNotify] private string? _authorizationError;

	/// <summary>The policy the Coldcard is actually enforcing, in the device's own words, and the hash
	/// identifying it. Read back from the device after it accepts a policy so the limits on screen can be
	/// compared with the ones configured here — the device is the thing doing the enforcing, so its account
	/// of the rules is the one that counts.</summary>
	[AutoNotify] private string? _devicePolicySummary;

	[AutoNotify] private string? _devicePolicyHash;

	public WalletCoinjoinModel(IServices services, Wallet wallet, CoinJoinManager coinjoinManager, WalletSettingsModel settings)
	{
		_services = services;
		_wallet = wallet;
		_settings = settings;
		_coinJoinManager = coinjoinManager;

		StatusUpdated = Observable
			.FromEventPattern<StatusChangedEventArgs>(_coinJoinManager, nameof(CoinJoinManager.StatusChanged))
			.Where(x => x.EventArgs.Wallet == wallet)
			.Select(x => x.EventArgs)
			.Where(x => x is WalletStartedCoinJoinEventArgs or WalletStoppedCoinJoinEventArgs or StartErrorEventArgs
				or CoinJoinStatusEventArgs or CompletedEventArgs or StartedEventArgs)
			.ObserveOn(RxApp.MainThreadScheduler);

		settings.WhenAnyValue(x => x.AutoCoinjoin)
				.Skip(1) // The first one is triggered at the creation.
				.DoAsync(async (autoCoinJoin) =>
				{
					if (autoCoinJoin)
					{
						await StartAsync(stopWhenAllMixed: false, false);
					}
					else
					{
						await StopAsync();
					}
				})
				.Subscribe();

		var coinjoinInputStarted =
			StatusUpdated.OfType<CoinJoinStatusEventArgs>()
						 .Where(e => e.CoinJoinProgressEventArgs is EnteringInputRegistrationPhase)
						 .Select(_ => true);

		var coinjoinStarted =
			StatusUpdated.OfType<StartedEventArgs>()
				.Select(_ => true);

		var coinjoinStopped =
			StatusUpdated.OfType<WalletStoppedCoinJoinEventArgs>()
				.Select(_ => false);

		var coinjoinCompleted =
			StatusUpdated.OfType<CompletedEventArgs>()
				.Select(_ => false);

		IsRunning =
			coinjoinInputStarted.Merge(coinjoinStopped)
				.Merge(coinjoinCompleted)
						   .ObserveOn(RxApp.MainThreadScheduler);

		IsRunning.BindTo(this, x => x.IsCoinjoining);

		IsStarted =
			coinjoinStarted.Merge(coinjoinStopped)
				.ObserveOn(RxApp.MainThreadScheduler);
	}

	public IObservable<StatusChangedEventArgs> StatusUpdated { get; }

	public IObservable<bool> IsRunning { get; }

	public IObservable<bool> IsStarted { get; }

	/// <summary>
	/// Asks the hardware wallet for the coinjoin authorization: Trezor shows the rounds and max fee rate for
	/// hold-to-confirm; Coldcard shows the HSM policy for approval. TrezorAuthorization drives both the
	/// authorization dialog and the music box text so the user knows to look at the device.
	/// </summary>
	public async Task<bool> AuthorizeHardwareAsync()
	{
		TrezorAuthorization = TrezorAuthorizationStatus.AwaitingConfirmation;
		AuthorizationError = null;
		try
		{
			await _wallet.AuthorizeHardwareCoinJoinAsync(
				_services.Config.CoordinatorIdentifier,
				_wallet.KeyManager.TrezorCoinjoinMaxRounds,
				new FeeRate(_wallet.KeyManager.TrezorCoinjoinMaxMiningFeeRate),
				CancellationToken.None);
			TrezorAuthorization = TrezorAuthorizationStatus.Confirmed;
			ReadDevicePolicy();
			return true;
		}
		catch (TrezorBridgeNotFoundException e)
		{
			Logger.LogWarning($"Trezor coinjoin authorization failed: {e.Message}");
			TrezorAuthorization = TrezorAuthorizationStatus.BridgeNotFound;
			return false;
		}
		catch (TrezorDeviceNotFoundException e)
		{
			Logger.LogWarning($"Hardware coinjoin authorization failed: {e.Message}");
			TrezorAuthorization = TrezorAuthorizationStatus.DeviceNotFound;
			return false;
		}
		catch (Exception e)
		{
			Logger.LogWarning($"Hardware coinjoin authorization failed: {e}");

			// ColdcardException and the wrong-device check carry text written for the user, naming the
			// setting to change or the device to swap. Anything else is an internal failure whose message
			// would not help, so those keep the generic wording. Prefer the short form where there is one:
			// the status line truncates, so a long message loses the instruction at its end.
			AuthorizationError = e switch
			{
				ColdcardException coldcard => coldcard.UserMessage ?? coldcard.Message,
				InvalidOperationException => e.Message,
				// Running on a platform with no transport for this device. Falling through to "press Play
				// to retry" sends the user round a loop that can never succeed - observed on macOS, where
				// the real reason sat in the log while the screen said nothing useful.
				PlatformNotSupportedException => "Not supported on this operating system",
				_ => null,
			};
			TrezorAuthorization = TrezorAuthorizationStatus.Failed;
			return false;
		}
	}

	/// <summary>Asks the device what policy it ended up with. Best-effort and non-fatal: this is here so a
	/// user can check the limits, and failing to read them must not undo an authorization that succeeded.</summary>
	private void ReadDevicePolicy()
	{
		if (_wallet.KeyChain is not ColdcardKeyChain coldcard)
		{
			return;
		}

		try
		{
			var status = coldcard.Device.GetHsmStatus();
			DevicePolicySummary = status.Summary;
			DevicePolicyHash = status.PolicyHash;
		}
		catch (Exception e)
		{
			Logger.LogDebug($"Could not read the Coldcard's active policy: {e.Message}");
		}
	}

	/// <param name="skipHardwareAuthorization">True when the caller already authorized through the dialog.</param>
	public async Task StartAsync(bool stopWhenAllMixed, bool overridePlebStop, bool skipHardwareAuthorization = false)
	{
		if (_wallet.KeyManager.IsHardwareCoinJoinWallet() && !skipHardwareAuthorization)
		{
			// Without the authorization no coinjoin can start.
			if (!await AuthorizeHardwareAsync().ConfigureAwait(false))
			{
				return;
			}
		}

		Wallet outputWallet = _services.GetWallets().First(x => x.WalletId == _settings.OutputWalletId);

		_coinJoinManager.RequestCoinJoinStart(_wallet, outputWallet, stopWhenAllMixed, overridePlebStop);
	}

	public async Task StopAsync()
	{
		_coinJoinManager.RequestCoinJoinStop(_wallet);
	}
}
