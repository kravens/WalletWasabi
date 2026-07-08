using NBitcoin;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.Infrastructure;
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
		try
		{
			await _wallet.AuthorizeHardwareCoinJoinAsync(
				_services.Config.CoordinatorIdentifier,
				_wallet.KeyManager.TrezorCoinjoinMaxRounds,
				new FeeRate(_wallet.KeyManager.TrezorCoinjoinMaxMiningFeeRate),
				CancellationToken.None);
			TrezorAuthorization = TrezorAuthorizationStatus.Confirmed;
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
			Logger.LogWarning($"Hardware coinjoin authorization failed: {e.Message}");
			TrezorAuthorization = TrezorAuthorizationStatus.Failed;
			return false;
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
