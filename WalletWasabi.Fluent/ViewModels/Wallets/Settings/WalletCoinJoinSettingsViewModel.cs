using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using NBitcoin;
using WalletWasabi.CoinJoinProfiles;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.Validation;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Settings;

[NavigationMetaData(
	Title = "Coinjoin Settings",
	Caption = "Display wallet coinjoin settings",
	IconName = "nav_wallet_24_regular",
	Order = 1,
	Category = "Wallet",
	Keywords = new[] { "Wallet", "Settings", },
	NavBarPosition = NavBarPosition.None,
	NavigationTarget = NavigationTarget.DialogScreen,
	Searchable = false)]
public partial class WalletCoinJoinSettingsViewModel : RoutableViewModel
{
	private readonly IWalletModel _wallet;

	[AutoNotify] private string _anonScoreTarget;
	[AutoNotify] private bool _nonPrivateCoinIsolation;
	[AutoNotify] private bool _allowPaymentsRegardlessOfAnonScore;
	[AutoNotify] private bool _maximizePrivacyProfileSelected;
	[AutoNotify] private bool _defaultProfileSelected;
	[AutoNotify] private bool _economicalProfileSelected;

	[AutoNotify] private bool _autoCoinJoin;
	[AutoNotify] private string _plebStopThreshold;
	[AutoNotify] private string _deviceMaxRounds;
	[AutoNotify] private string _deviceMaxMiningFeeRate;
	[AutoNotify] private string _devicePolicyMaxSatsLeaving;
	[AutoNotify] private string _devicePolicyMaxTransactionsPerPeriod;
	[AutoNotify] private string _devicePolicyMinRoundInputs;
	[AutoNotify] private bool _devicePolicyOutOfSync;
	[AutoNotify] private string _devicePolicySummary = "";
	[AutoNotify] private string _devicePolicyHash = "";
	[AutoNotify] private bool _isOutputWalletSelectionEnabled = true;
	[AutoNotify] private IWalletModel _selectedOutputWallet;
	[AutoNotify] private ReadOnlyObservableCollection<IWalletModel> _wallets = ReadOnlyObservableCollection<IWalletModel>.Empty;

	private CompositeDisposable _disposable = new();

	public WalletCoinJoinSettingsViewModel(UiContext uiContext, IWalletModel walletModel) : base(uiContext)
	{
		_wallet = walletModel;
		_autoCoinJoin = _wallet.Settings.AutoCoinjoin;
		_plebStopThreshold = _wallet.Settings.PlebStopThreshold.ToString();
		_anonScoreTarget = _wallet.Settings.AnonScoreTarget.ToString();
		_nonPrivateCoinIsolation = _wallet.Settings.NonPrivateCoinIsolation;
		_allowPaymentsRegardlessOfAnonScore = _wallet.Settings.AllowPaymentsRegardlessOfAnonScore;
		HasDeviceAuthorizationLimits = _wallet.CoinJoinNeedsDeviceAuthorization;
		_deviceMaxRounds = _wallet.Settings.CoinJoinDeviceMaxRounds.ToString();
		_devicePolicyMaxSatsLeaving = _wallet.Settings.DevicePolicyMaxSatsLeaving.ToString(CultureInfo.InvariantCulture);
		_devicePolicyMaxTransactionsPerPeriod = _wallet.Settings.DevicePolicyMaxTransactionsPerPeriod.ToString(CultureInfo.InvariantCulture);
		_devicePolicyMinRoundInputs = _wallet.Settings.DevicePolicyMinRoundInputs.ToString(CultureInfo.InvariantCulture);
		_devicePolicyOutOfSync = _wallet.Settings.IsDevicePolicyOutOfSync;

		// What the device says it is enforcing, as opposed to what is configured above. Only populated once a
		// policy has been accepted, so it stays hidden until there is something real to show.
		if (_wallet.Coinjoin is { } coinjoin)
		{
			coinjoin.WhenAnyValue(x => x.DevicePolicySummary).BindTo(this, x => x.DevicePolicySummary);
			coinjoin.WhenAnyValue(x => x.DevicePolicyHash).BindTo(this, x => x.DevicePolicyHash);
		}
		HasDevicePolicyLimits = _wallet.Settings.HasDevicePolicyLimits;
		CoinJoinLimitsEnforcedBy = _wallet.Settings.CoinJoinLimitsEnforcedBy;
		_deviceMaxMiningFeeRate = _wallet.Settings.CoinJoinDeviceMaxMiningFeeRate.ToString(System.Globalization.CultureInfo.InvariantCulture);

		_selectedOutputWallet = UiContext.WalletRepository.Wallets.Items.First(x => x.Id == _wallet.Settings.OutputWalletId);

		SetupCancel(enableCancel: false, enableCancelOnEscape: true, enableCancelOnPressed: true);

		NextCommand = CancelCommand;

		SetAutoCoinJoin = ReactiveCommand.CreateFromTask(
			() =>
			{
				_wallet.Settings.AutoCoinjoin = AutoCoinJoin;
				_wallet.Settings.Save();
				return Task.CompletedTask;
			});

		SetNonPrivateCoinIsolationCommand = ReactiveCommand.CreateFromTask(() =>
		{
			_wallet.Settings.NonPrivateCoinIsolation = NonPrivateCoinIsolation;
			_wallet.Settings.Save();
			return Task.CompletedTask;
		});

		SetAllowPaymentsRegardlessOfAnonScoreCommand = ReactiveCommand.CreateFromTask(() =>
		{
			_wallet.Settings.AllowPaymentsRegardlessOfAnonScore = AllowPaymentsRegardlessOfAnonScore;
			_wallet.Settings.Save();
			return Task.CompletedTask;
		});

		SelectMaximizePrivacySettings = ReactiveCommand.CreateFromTask(() => SetProfile("MaximizePrivacy"));

		SelectDefaultSettings = ReactiveCommand.CreateFromTask(() => SetProfile("Default"));

		SelectEconomicalSettings = ReactiveCommand.CreateFromTask(() => SetProfile("Economical"));

		this.WhenAnyValue(
				x => x.AnonScoreTarget,
				x => x.NonPrivateCoinIsolation,
				x => x.AllowPaymentsRegardlessOfAnonScore)
			.ObserveOn(RxApp.TaskpoolScheduler)
			.Subscribe(_ =>
			{
				var selectedProfile = PrivacyProfiles.Profiles
					.FirstOrDefault(p =>
						p.Equals(
							int.TryParse(AnonScoreTarget, out var anonScoreTarget) ? anonScoreTarget : 0,
							NonPrivateCoinIsolation,
							AllowPaymentsRegardlessOfAnonScore));

				MaximizePrivacyProfileSelected = selectedProfile?.Name == "MaximizePrivacy";
				EconomicalProfileSelected = selectedProfile?.Name == "Economical";
				DefaultProfileSelected = selectedProfile?.Name == "Default";
			});

		this.ValidateProperty(x => x.AnonScoreTarget, ValidateAnonScoreTarget);
		this.ValidateProperty(x => x.DeviceMaxRounds, ValidateDeviceMaxRounds);
		this.ValidateProperty(x => x.DeviceMaxMiningFeeRate, ValidateDeviceMaxMiningFeeRate);
		this.ValidateProperty(x => x.DevicePolicyMaxSatsLeaving, ValidateDevicePolicyMaxSatsLeaving);
		this.ValidateProperty(x => x.DevicePolicyMaxTransactionsPerPeriod, ValidateDevicePolicyMaxTransactionsPerPeriod);
		this.ValidateProperty(x => x.DevicePolicyMinRoundInputs, ValidateDevicePolicyMinRoundInputs);

		this.WhenAnyValue(x => x.PlebStopThreshold)
			.Skip(1)
			.Throttle(TimeSpan.FromMilliseconds(1000))
			.ObserveOn(RxApp.TaskpoolScheduler)
			.Subscribe(
				x =>
				{
					if (Money.TryParse(x, out var result) && result != _wallet.Settings.PlebStopThreshold)
					{
						_wallet.Settings.PlebStopThreshold = result;
						_wallet.Settings.Save();
					}
				});

		this.WhenAnyValue(x => x.SelectedOutputWallet)
			.Skip(1)
			.ObserveOn(RxApp.TaskpoolScheduler)
			.Subscribe(x => _wallet.Settings.OutputWalletId = x.Id);

		walletModel.IsCoinjoinStarted
			.Select(isRunning => !isRunning)
			.BindTo(this, x => x.IsOutputWalletSelectionEnabled);

		ManuallyUpdateOutputWalletList();
	}

	/// <summary>Whether the device authorization limits apply to this wallet, so they can be edited.</summary>
	public bool HasDeviceAuthorizationLimits { get; }

	public ICommand SetAutoCoinJoin { get; }
	public ICommand SetNonPrivateCoinIsolationCommand { get; }
	public ICommand SetAllowPaymentsRegardlessOfAnonScoreCommand { get; }
	public ICommand SelectMaximizePrivacySettings { get; }
	public ICommand SelectDefaultSettings { get; }
	public ICommand SelectEconomicalSettings { get; }

	public void ManuallyUpdateOutputWalletList()
	{
		_disposable.Dispose();
		_disposable = new CompositeDisposable();

		UiContext.WalletRepository.Wallets
			.Connect()
			.AutoRefresh(x => x.IsLoaded)
			.Filter(x => (x.Id == _wallet.Id || x.Settings.OutputWalletId != _wallet.Id) && x.IsLoaded)
			.SortBy(i => i.Name)
			.Bind(out var wallets)
			.Subscribe()
			.DisposeWith(_disposable);

		_wallets = wallets;
	}

	private void ValidateAnonScoreTarget(IValidationErrors errors)
	{
		if (int.TryParse(AnonScoreTarget, out var anonScoreTarget))
		{
			if (anonScoreTarget is < PrivacyProfiles.AbsoluteMinAnonScoreTarget or > PrivacyProfiles.AbsoluteMaxAnonScoreTarget)
			{
				errors.Add(ErrorSeverity.Error, $"Must be between {PrivacyProfiles.AbsoluteMinAnonScoreTarget} and {PrivacyProfiles.AbsoluteMaxAnonScoreTarget}");
			}
			else
			{
				_wallet.Settings.AnonScoreTarget = anonScoreTarget;
				_wallet.Settings.Save();
			}
		}
		else
		{
			errors.Add(ErrorSeverity.Error, $"Must be a number between {PrivacyProfiles.AbsoluteMinAnonScoreTarget} and {PrivacyProfiles.AbsoluteMaxAnonScoreTarget}");
		}
	}

	/// <summary>Whether the device runs a policy of its own, which is what these extra limits belong to.</summary>
	public bool HasDevicePolicyLimits { get; }

	/// <summary>Which of these limits the device enforces and which Wasabi does, in the user's own words.</summary>
	public string CoinJoinLimitsEnforcedBy { get; } = "";

	private void ValidateDevicePolicyMaxSatsLeaving(IValidationErrors errors)
	{
		// A round costs the wallet its fee share, so a cap under a few thousand sats would refuse everything.
		// The upper end is a whole bitcoin, past which the cap stops being a cap.
		if (long.TryParse(DevicePolicyMaxSatsLeaving, NumberStyles.Number, CultureInfo.InvariantCulture, out var sats)
			&& sats is >= 1_000 and <= 100_000_000)
		{
			_wallet.Settings.DevicePolicyMaxSatsLeaving = sats;
			_wallet.Settings.Save();
			RefreshDevicePolicySync();
		}
		else
		{
			errors.Add(ErrorSeverity.Error, "Must be between 1,000 and 100,000,000 sats.");
		}
	}

	private void ValidateDevicePolicyMaxTransactionsPerPeriod(IValidationErrors errors)
	{
		if (int.TryParse(DevicePolicyMaxTransactionsPerPeriod, out var count) && count is >= 1 and <= 500)
		{
			_wallet.Settings.DevicePolicyMaxTransactionsPerPeriod = count;
			_wallet.Settings.Save();
			RefreshDevicePolicySync();
		}
		else
		{
			errors.Add(ErrorSeverity.Error, "Must be a whole number between 1 and 500.");
		}
	}

	private void ValidateDevicePolicyMinRoundInputs(IValidationErrors errors)
	{
		// 0 turns the device-side floor off; above that it is the fewest participants the device will sign with.
		if (int.TryParse(DevicePolicyMinRoundInputs, out var inputs) && inputs is >= 0 and <= 500)
		{
			_wallet.Settings.DevicePolicyMinRoundInputs = inputs;
			_wallet.Settings.Save();
			RefreshDevicePolicySync();
		}
		else
		{
			errors.Add(ErrorSeverity.Error, "Must be a whole number between 0 and 500.");
		}
	}

	/// <summary>Re-reads whether the saved limits still match the policy the device is enforcing. Called after
	/// each validator, since each one may have just written a value that puts them out of step.</summary>
	private void RefreshDevicePolicySync() =>
		DevicePolicyOutOfSync = _wallet.Settings.IsDevicePolicyOutOfSync;

	private void ValidateDeviceMaxRounds(IValidationErrors errors)
	{
		if (!int.TryParse(DeviceMaxRounds, out var rounds))
		{
			errors.Add(ErrorSeverity.Error, "Must be a whole number.");
			return;
		}

		if (!HardwareWalletService.TryValidateMaxRounds(rounds, out var error))
		{
			errors.Add(ErrorSeverity.Error, error);
			return;
		}

		_wallet.Settings.CoinJoinDeviceMaxRounds = rounds;
		_wallet.Settings.Save();
	}

	private void ValidateDeviceMaxMiningFeeRate(IValidationErrors errors)
	{
		if (!decimal.TryParse(DeviceMaxMiningFeeRate, NumberStyles.Number, CultureInfo.InvariantCulture, out var feeRate))
		{
			errors.Add(ErrorSeverity.Error, "Must be a fee rate in sat/vByte.");
			return;
		}

		if (!HardwareWalletService.TryValidateMaxMiningFeeRate(feeRate, out var error))
		{
			errors.Add(ErrorSeverity.Error, error);
			return;
		}

		_wallet.Settings.CoinJoinDeviceMaxMiningFeeRate = feeRate;
		_wallet.Settings.Save();
	}

	private Task SetProfile(string profileName)
	{
		var profile = PrivacyProfiles.Profiles.FirstOrDefault(p => p.Name == profileName);
		if (profile is null)
		{
			return Task.CompletedTask;
		}

		AnonScoreTarget = profile.AnonScoreTarget.ToString();
		_wallet.Settings.AnonScoreTarget = profile.AnonScoreTarget;

		NonPrivateCoinIsolation = profile.NonPrivateCoinIsolation;
		_wallet.Settings.NonPrivateCoinIsolation = profile.NonPrivateCoinIsolation;

		AllowPaymentsRegardlessOfAnonScore = profile.AllowPaymentsRegardlessOfAnonScore;
		_wallet.Settings.AllowPaymentsRegardlessOfAnonScore = profile.AllowPaymentsRegardlessOfAnonScore;

		_wallet.Settings.Save();
		return Task.CompletedTask;
	}
}
