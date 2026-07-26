using NBitcoin;
using ReactiveUI;
using System.Reactive.Linq;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Helpers;
using WalletWasabi.Hwi;
using WalletWasabi.Hwi.Trezor;
using WalletWasabi.Models;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.Models.Wallets;

[AppLifetime]
public partial class WalletSettingsModel : ReactiveObject
{
	private readonly IServices _services;
	private readonly KeyManager _keyManager;
	private bool _isDirty;

	[AutoNotify] private bool _isNewWallet;
	[AutoNotify] private bool _autoCoinjoin;
	[AutoNotify] private bool _preferPsbtWorkflow;
	[AutoNotify] private Money _plebStopThreshold;
	[AutoNotify] private int _anonScoreTarget;
	[AutoNotify] private int _trezorCoinjoinMaxRounds;
	[AutoNotify] private decimal _trezorCoinjoinMaxMiningFeeRate;
	[AutoNotify] private double _coldcardMinSelfTransferPercent;
	[AutoNotify] private long _coldcardMaxSatsLeaving;
	[AutoNotify] private int _coldcardMaxTransactionsPerPeriod;
	[AutoNotify] private bool _nonPrivateCoinIsolation;
	[AutoNotify] private WalletId? _outputWalletId;
	[AutoNotify] private ScriptType _defaultReceiveScriptType;
	[AutoNotify] private PreferredScriptPubKeyType _changeScriptPubKeyType;
	[AutoNotify] private SendWorkflow _defaultSendWorkflow;

	public WalletSettingsModel(IServices services, KeyManager keyManager, bool isNewWallet = false, bool isCoinJoinPaused = false)
	{
		_services = services;
		_keyManager = keyManager;

		_isNewWallet = isNewWallet;
		_isDirty = isNewWallet;
		IsCoinJoinPaused = isCoinJoinPaused;

		_autoCoinjoin = _keyManager.AutoCoinJoin;
		_preferPsbtWorkflow = _keyManager.PreferPsbtWorkflow;
		_plebStopThreshold = _keyManager.PlebStopThreshold ?? KeyManager.DefaultPlebStopThreshold;
		_anonScoreTarget = _keyManager.AnonScoreTarget;
		_trezorCoinjoinMaxRounds = _keyManager.TrezorCoinjoinMaxRounds;
		_trezorCoinjoinMaxMiningFeeRate = _keyManager.TrezorCoinjoinMaxMiningFeeRate;
		_coldcardMinSelfTransferPercent = _keyManager.ColdcardMinSelfTransferPercent;
		_coldcardMaxSatsLeaving = _keyManager.ColdcardMaxSatsLeaving;
		_coldcardMaxTransactionsPerPeriod = _keyManager.ColdcardMaxTransactionsPerPeriod;
		_nonPrivateCoinIsolation = _keyManager.NonPrivateCoinIsolation;

		if (!isNewWallet)
		{
			_outputWalletId = services.GetWalletByName(_keyManager.WalletName).WalletId;
		}

		_defaultReceiveScriptType = ScriptType.FromEnum(_keyManager.DefaultReceiveScriptType);
		_changeScriptPubKeyType = _keyManager.ChangeScriptPubKeyType;
		_defaultSendWorkflow = _keyManager.DefaultSendWorkflow;

		WalletType = WalletHelpers.GetType(_keyManager);

		this.WhenAnyValue(
				x => x.AutoCoinjoin,
				x => x.PreferPsbtWorkflow,
				x => x.PlebStopThreshold,
				x => x.AnonScoreTarget,
				x => x.NonPrivateCoinIsolation)
			.Skip(1)
			.Do(_ => SetValues())
			.Subscribe();

		this.WhenAnyValue(
				x => x.TrezorCoinjoinMaxRounds,
				x => x.TrezorCoinjoinMaxMiningFeeRate,
				x => x.ColdcardMinSelfTransferPercent,
				x => x.ColdcardMaxSatsLeaving,
				x => x.ColdcardMaxTransactionsPerPeriod)
			.Skip(1)
			.Do(_ => SetValues())
			.Subscribe();

		this.WhenAnyValue(
				x => x.DefaultSendWorkflow,
				x => x.DefaultReceiveScriptType,
				x => x.ChangeScriptPubKeyType)
			.Do(_ => SetValues())
			.Subscribe();
	}

	public WalletType WalletType { get; }

	public bool IsTrezorCoinJoinWallet => _keyManager.IsTrezorCoinJoinWallet();

	/// <summary>Any device-signed coinjoin wallet. The round budget and fee-rate cap apply to all of
	/// them, so the settings that control those must be visible for all of them.</summary>
	public bool IsHardwareCoinJoinWallet => _keyManager.IsHardwareCoinJoinWallet();

	/// <summary>Whether this wallet is set up to coinjoin at all, ignoring whether it is currently switched
	/// on. The switch has to key on this rather than on the live state, or turning coinjoin off would hide
	/// the control that turns it back on.</summary>
	public bool HasCoinJoinCapability => _keyManager.IsHardwareWallet
		&& (_keyManager.CoinJoinVendor != HardwareCoinJoinVendor.None || _keyManager.IsTrezorCoinJoinWallet());

	/// <summary>Coinjoin on a hardware wallet is opt-in; this is the opt-out, mirroring the PSBT workflow
	/// switch beside it.</summary>
	public bool CoinJoinEnabled
	{
		get => !_keyManager.CoinJoinDisabled;
		set
		{
			_keyManager.CoinJoinDisabled = !value;
			_isDirty = true;
		}
	}

	/// <summary>Only a Coldcard enforces a self-transfer floor, so only it shows that setting.</summary>
	public bool IsColdcardCoinJoinWallet => _keyManager.GetCoinJoinVendor() == HardwareCoinJoinVendor.Coldcard;

	/// <summary>
	/// Where the round budget and fee cap are actually enforced, which differs by vendor and must not be
	/// misrepresented: a Trezor shows both on screen and enforces them itself, while a Coldcard's HSM
	/// policy has no concept of either, so Wasabi enforces them and the device only bounds how much value
	/// can leave in one transaction. Telling a Coldcard user their device confirms these limits would be a
	/// false claim, and so would implying the floor bounds the total — it applies per transaction.
	/// </summary>
	public string CoinJoinLimitsEnforcedBy => _keyManager.GetCoinJoinVendor() switch
	{
		HardwareCoinJoinVendor.Trezor => "Shown on the device and confirmed there; the device enforces both.",
		HardwareCoinJoinVendor.Coldcard => "The fee-rate cap is enforced by Wasabi — the Coldcard's HSM policy has no concept of one. The limits below it are enforced by the device: how much of your value may leave in a single transaction, both as a share and as an amount, and how many transactions it will sign in total and per period.",
		_ => "Enforced by Wasabi for this device.",
	};

	public bool IsCoinJoinPaused { get; set; }

	/// <summary>
	/// Saves to current configuration to file.
	/// </summary>
	/// <returns>The unique ID of the wallet.</returns>
	public WalletId Save()
	{
		if (_isDirty)
		{
			_keyManager.ToFile();

			if (IsNewWallet)
			{
				_services.AddWallet(_keyManager);
				IsNewWallet = false;
				OutputWalletId = _services.GetWalletByName(_keyManager.WalletName).WalletId;
			}

			_isDirty = false;
		}

		return _services.GetWalletByName(_keyManager.WalletName).WalletId;
	}

	private void SetValues()
	{
		_keyManager.AutoCoinJoin = AutoCoinjoin;
		_keyManager.PreferPsbtWorkflow = PreferPsbtWorkflow;
		_keyManager.PlebStopThreshold = PlebStopThreshold;
		_keyManager.AnonScoreTarget = AnonScoreTarget;
		_keyManager.TrezorCoinjoinMaxRounds = TrezorCoinjoinMaxRounds;
		_keyManager.TrezorCoinjoinMaxMiningFeeRate = TrezorCoinjoinMaxMiningFeeRate;
		_keyManager.ColdcardMinSelfTransferPercent = ColdcardMinSelfTransferPercent;
		_keyManager.ColdcardMaxSatsLeaving = ColdcardMaxSatsLeaving;
		_keyManager.ColdcardMaxTransactionsPerPeriod = ColdcardMaxTransactionsPerPeriod;
		_keyManager.NonPrivateCoinIsolation = NonPrivateCoinIsolation;
		_keyManager.DefaultSendWorkflow = DefaultSendWorkflow;
		_keyManager.DefaultReceiveScriptType = ScriptType.ToScriptPubKeyType(DefaultReceiveScriptType);
		_keyManager.ChangeScriptPubKeyType = ChangeScriptPubKeyType;
		_isDirty = true;
	}

	public void RescanWallet(uint startingHeight = 0)
	{
		_keyManager.SetBestHeight(startingHeight + Constants.ResyncHeightMargin);
	}
}
