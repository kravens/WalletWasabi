using System.Collections.ObjectModel;
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
	[AutoNotify] private bool _maximizePrivacyProfileSelected;
	[AutoNotify] private bool _defaultProfileSelected;
	[AutoNotify] private bool _economicalProfileSelected;

	[AutoNotify] private bool _autoCoinJoin;
	[AutoNotify] private string _plebStopThreshold;
	[AutoNotify] private string _trezorMaxRounds;
	[AutoNotify] private string _trezorMaxMiningFeeRate;
	[AutoNotify] private string _coldcardMinSelfTransfer;
	[AutoNotify] private string _coldcardMaxSatsLeaving;
	[AutoNotify] private string _coldcardMaxTxnPerPeriod;
	[AutoNotify] private string? _devicePolicySummary;
	[AutoNotify] private string? _devicePolicyHash;
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
		IsTrezorCoinJoinWallet = _wallet.Settings.IsTrezorCoinJoinWallet;
		IsHardwareCoinJoinWallet = _wallet.Settings.IsHardwareCoinJoinWallet;
		CoinJoinLimitsEnforcedBy = _wallet.Settings.CoinJoinLimitsEnforcedBy;
		_trezorMaxRounds = _wallet.Settings.TrezorCoinjoinMaxRounds.ToString();
		_trezorMaxMiningFeeRate = _wallet.Settings.TrezorCoinjoinMaxMiningFeeRate.ToString(System.Globalization.CultureInfo.InvariantCulture);
		IsColdcardCoinJoinWallet = _wallet.Settings.IsColdcardCoinJoinWallet;

		// What the device says it is enforcing, as opposed to what is configured above. Only populated
		// once a policy has been accepted, so it stays hidden until there is something real to show.
		if (_wallet.Coinjoin is { } coinjoin)
		{
			coinjoin.WhenAnyValue(x => x.DevicePolicySummary)
				.BindTo(this, x => x.DevicePolicySummary);
			coinjoin.WhenAnyValue(x => x.DevicePolicyHash)
				.BindTo(this, x => x.DevicePolicyHash);
		}
		_coldcardMinSelfTransfer = _wallet.Settings.ColdcardMinSelfTransferPercent.ToString(System.Globalization.CultureInfo.InvariantCulture);
		_coldcardMaxSatsLeaving = _wallet.Settings.ColdcardMaxSatsLeaving.ToString(System.Globalization.CultureInfo.InvariantCulture);
		_coldcardMaxTxnPerPeriod = _wallet.Settings.ColdcardMaxTransactionsPerPeriod.ToString(System.Globalization.CultureInfo.InvariantCulture);

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

		SelectMaximizePrivacySettings = ReactiveCommand.CreateFromTask(() => SetProfile("MaximizePrivacy"));

		SelectDefaultSettings = ReactiveCommand.CreateFromTask(() => SetProfile("Default"));

		SelectEconomicalSettings = ReactiveCommand.CreateFromTask(() => SetProfile("Economical"));

		this.WhenAnyValue(
				x => x.AnonScoreTarget,
				x => x.NonPrivateCoinIsolation)
			.ObserveOn(RxApp.TaskpoolScheduler)
			.Subscribe(_ =>
			{
				var selectedProfile = PrivacyProfiles.Profiles
					.FirstOrDefault(p =>
						p.Equals(
							int.TryParse(AnonScoreTarget, out var anonScoreTarget) ? anonScoreTarget : 0,
							NonPrivateCoinIsolation));

				MaximizePrivacyProfileSelected = selectedProfile?.Name == "MaximizePrivacy";
				EconomicalProfileSelected = selectedProfile?.Name == "Economical";
				DefaultProfileSelected = selectedProfile?.Name == "Default";
			});

		this.ValidateProperty(x => x.AnonScoreTarget, ValidateAnonScoreTarget);
		this.ValidateProperty(x => x.TrezorMaxRounds, ValidateTrezorMaxRounds);
		this.ValidateProperty(x => x.TrezorMaxMiningFeeRate, ValidateTrezorMaxMiningFeeRate);
		this.ValidateProperty(x => x.ColdcardMinSelfTransfer, ValidateColdcardMinSelfTransfer);
		this.ValidateProperty(x => x.ColdcardMaxSatsLeaving, ValidateColdcardMaxSatsLeaving);
		this.ValidateProperty(x => x.ColdcardMaxTxnPerPeriod, ValidateColdcardMaxTxnPerPeriod);

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

	public bool IsTrezorCoinJoinWallet { get; }

	/// <summary>The round budget and fee cap bind every device-signed wallet, so they are shown for all
	/// of them — hiding them left Coldcard users with an invisible 5 sat/vByte cap they could not change.</summary>
	public bool IsHardwareCoinJoinWallet { get; }

	/// <summary>Only a Coldcard has a self-transfer floor; the setting must not appear for other vendors.</summary>
	public bool IsColdcardCoinJoinWallet { get; }

	/// <summary>Who actually enforces those two limits, worded per vendor so the UI never implies a
	/// device is guaranteeing something the host is doing.</summary>
	public string CoinJoinLimitsEnforcedBy { get; } = "";

	public ICommand SetAutoCoinJoin { get; }
	public ICommand SetNonPrivateCoinIsolationCommand { get; }
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

	private void ValidateTrezorMaxRounds(IValidationErrors errors)
	{
		// Firmware caps max_rounds at 500 under strict safety checks; keep a sane user-facing range.
		if (int.TryParse(TrezorMaxRounds, out var rounds) && rounds is >= 1 and <= 500)
		{
			_wallet.Settings.TrezorCoinjoinMaxRounds = rounds;
			_wallet.Settings.Save();
		}
		else
		{
			errors.Add(ErrorSeverity.Error, "Must be a whole number between 1 and 500.");
		}
	}

	private void ValidateTrezorMaxMiningFeeRate(IValidationErrors errors)
	{
		if (decimal.TryParse(TrezorMaxMiningFeeRate, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var feeRate) && feeRate is > 0 and <= 10000)
		{
			_wallet.Settings.TrezorCoinjoinMaxMiningFeeRate = feeRate;
			_wallet.Settings.Save();
		}
		else
		{
			errors.Add(ErrorSeverity.Error, "Must be a positive fee rate in sat/vByte.");
		}
	}

	private void ValidateColdcardMinSelfTransfer(IValidationErrors errors)
	{
		// Floor of 50: below that the device would be waving through transactions that hand half the
		// wallet away, which is not a coinjoin. The ceiling is just under 100 because a round always
		// costs something in fees, so exactly 100 could never be met.
		if (double.TryParse(ColdcardMinSelfTransfer, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var percent)
			&& percent is >= 50 and <= 99.9)
		{
			_wallet.Settings.ColdcardMinSelfTransferPercent = percent;
			_wallet.Settings.Save();
		}
		else
		{
			errors.Add(ErrorSeverity.Error, "Must be between 50 and 99.9 percent.");
		}
	}

	private void ValidateColdcardMaxSatsLeaving(IValidationErrors errors)
	{
		// A round costs the wallet its fee share, so a cap under a few thousand sats would refuse
		// everything. The upper end is a whole bitcoin, past which the cap stops being a cap.
		if (long.TryParse(ColdcardMaxSatsLeaving, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var sats)
			&& sats is >= 1_000 and <= 100_000_000)
		{
			_wallet.Settings.ColdcardMaxSatsLeaving = sats;
			_wallet.Settings.Save();
		}
		else
		{
			errors.Add(ErrorSeverity.Error, "Must be between 1,000 and 100,000,000 sats.");
		}
	}

	private void ValidateColdcardMaxTxnPerPeriod(IValidationErrors errors)
	{
		if (int.TryParse(ColdcardMaxTxnPerPeriod, out var count) && count is >= 1 and <= 500)
		{
			_wallet.Settings.ColdcardMaxTransactionsPerPeriod = count;
			_wallet.Settings.Save();
		}
		else
		{
			errors.Add(ErrorSeverity.Error, "Must be a whole number between 1 and 500.");
		}
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

		_wallet.Settings.Save();
		return Task.CompletedTask;
	}
}
