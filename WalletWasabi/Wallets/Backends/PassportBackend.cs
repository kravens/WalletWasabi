using WalletWasabi.Hwi;
using WalletWasabi.Hwi.Models;
using WalletWasabi.Hwi.Passport;
using WalletWasabi.Models;
using WalletWasabi.WabiSabi.Client;

namespace WalletWasabi.Wallets.Backends;

/// <summary>
/// Foundation Passport Prime, reached over its own framing on USB HID. Like a Coldcard it signs from the
/// wallet's ordinary accounts, and like a Trezor the user approves a batch up front, here as a session policy
/// the device holds for a fixed time and a fixed fee budget. HWI cannot see the device, so it is detected
/// and imported over the same transport.
/// </summary>
internal class PassportBackend : IHardwareWalletBackend
{
	/// <summary>Our inputs plus our outputs of one round, in vbytes, generously; the wallet's fee-rate cap over this many rounds is the session's fee budget.</summary>
	private const int RoundVsizeBudget = 1000;
	internal static readonly TimeSpan SessionValidity = TimeSpan.FromHours(12);

	public PassportBackend(Network network, Func<IReadOnlyList<string>>? enumerate = null, Func<IPassportDevice>? open = null)
	{
		_network = network;
		_enumerate = enumerate ?? PassportUsb.Enumerate;
		_open = open ?? (() => PassportDevice.Open());
	}

	private readonly Network _network;
	private readonly Func<IReadOnlyList<string>> _enumerate;
	private readonly Func<IPassportDevice> _open;

	public HardwareCoinJoinVendor Vendor => HardwareCoinJoinVendor.PassportPrime;

	public bool IsUnknownToHwi => true;

	public Task<bool> IsTransportAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(_enumerate().Count > 0);

	public async Task<HwiEnumerateEntry?> TryDetectAsync(CancellationToken cancellationToken)
	{
		if (_enumerate().FirstOrDefault() is not { } serial)
		{
			return null;
		}

		var (fingerprint, _) = await ReadAccountAsync(ScriptPubKeyType.Segwit, cancellationToken).ConfigureAwait(false);
		return new HwiEnumerateEntry(HardwareWalletModels.Foundation_Passport, serial, null, fingerprint, false, false, null, null);
	}

	/// <summary>
	/// Reads both accounts from the device, whose fingerprints have to agree: computed on the device over a
	/// direct cable, that is the trust an HWI import has. The device has no command to show an address, so
	/// the first receive address is reported for the user to check against the device by hand.
	/// </summary>
	public async Task<KeyManager?> TryImportAsync(HDFingerprint? masterFingerprint, string walletFilePath, bool enableCoinjoin, IProgress<BitcoinAddress>? addressToConfirm, CancellationToken cancellationToken)
	{
		if (_enumerate().Count == 0)
		{
			return null; // no Passport; another vendor's device may be the one connected
		}

		var (segwitFingerprint, segwitExtPubKey) = await ReadAccountAsync(ScriptPubKeyType.Segwit, cancellationToken).ConfigureAwait(false);
		var (taprootFingerprint, taprootExtPubKey) = await ReadAccountAsync(ScriptPubKeyType.TaprootBIP86, cancellationToken).ConfigureAwait(false);
		if (segwitFingerprint != taprootFingerprint)
		{
			throw new HardwareWalletException("The device handed out accounts of two different seeds. Nothing was imported.");
		}
		if (masterFingerprint is { } expected && expected != segwitFingerprint)
		{
			throw new HardwareWalletException($"The connected Passport has fingerprint {segwitFingerprint}, expected {expected}.");
		}

		var taprootAccountKeyPath = KeyManager.GetAccountKeyPath(_network, ScriptPubKeyType.TaprootBIP86);
		var keyManager = KeyManager.CreateNewHardwareWalletWatchOnly(segwitFingerprint, segwitExtPubKey, taprootExtPubKey, null, null, _network, walletFilePath, taprootAccountKeyPath);
		keyManager.CoinJoinVendor = HardwareCoinJoinVendor.PassportPrime;
		keyManager.CoinJoinDisabled = !enableCoinjoin;
		keyManager.SetIcon(WalletType.Hardware);
		keyManager.ToFile();

		addressToConfirm?.Report(segwitExtPubKey.Derive(0).Derive(0).PubKey.GetAddress(ScriptPubKeyType.Segwit, _network));
		return keyManager;
	}

	private Task<(HDFingerprint Fingerprint, ExtPubKey ExtPubKey)> ReadAccountAsync(ScriptPubKeyType scriptPubKeyType, CancellationToken cancellationToken) =>
		Task.Run(() =>
		{
			using var device = _open();
			return device.GetXpub(KeyManager.GetAccountKeyPath(_network, scriptPubKeyType), _network);
		}, cancellationToken);

	public async Task<IKeyChain> AuthorizeCoinJoinAsync(
		KeyManager keyManager,
		IKeyChain? existingKeyChain,
		string coordinatorIdentifier,
		int maxRounds,
		FeeRate maxMiningFeeRate,
		CancellationToken cancellationToken)
	{
		if (existingKeyChain is { NeedsReauthorization: false } and PassportKeyChain live && live.Device.IsAlive())
		{
			live.MaxMiningFeeRate = maxMiningFeeRate;
			return live;
		}

		// Spent, expired or unplugged: disposing revokes whatever the device still holds of it.
		(existingKeyChain as IDisposable)?.Dispose();

		var policy = ComposePolicy(_network, coordinatorIdentifier, maxRounds, maxMiningFeeRate);
		var device = await Task.Run(_open, cancellationToken).ConfigureAwait(false);
		try
		{
			// The user is about to approve a policy on a screen; make sure it is this wallet's device first.
			var (fingerprint, _) = device.GetXpub(KeyManager.GetAccountKeyPath(_network, ScriptPubKeyType.Segwit), _network);
			if (keyManager.MasterFingerprint != fingerprint)
			{
				throw new HardwareWalletException($"The connected Passport has fingerprint {fingerprint}, expected {keyManager.MasterFingerprint}.");
			}

			var token = await Task.Run(() => device.AuthorizeCoinJoin(policy), cancellationToken).ConfigureAwait(false);
			return new PassportKeyChain(device, token, keyManager, policy.MaxRounds, DateTimeOffset.UtcNow + SessionValidity) { MaxMiningFeeRate = maxMiningFeeRate };
		}
		catch
		{
			device.Dispose();
			throw;
		}
	}

	/// <summary>The session the user approves on the device: this wallet's account, this coordinator, the wallet's round budget, and as fee budget the wallet's fee-rate cap applied to every round of it.</summary>
	internal static CoinjoinPolicy ComposePolicy(Network network, string coordinatorIdentifier, int maxRounds, FeeRate maxMiningFeeRate)
	{
		var rounds = (ushort)Math.Clamp(maxRounds, 1, ushort.MaxValue);
		return new CoinjoinPolicy
		{
			Network = network,
			Account = 0,
			CoordinatorIdentifier = coordinatorIdentifier,
			FeeBudgetSats = (ulong)maxMiningFeeRate.GetFee(RoundVsizeBudget).Satoshi * rounds,
			MaxRounds = rounds,
			ValidForSeconds = (uint)SessionValidity.TotalSeconds,
		};
	}

	public void Dispose()
	{
	}
}
