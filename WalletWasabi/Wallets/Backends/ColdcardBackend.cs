using System.Collections.Concurrent;
using System.Linq;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Hwi;
using WalletWasabi.Hwi.Coldcard;
using WalletWasabi.Logging;
using WalletWasabi.WabiSabi.Client;

namespace WalletWasabi.Wallets.Backends;

/// <summary>
/// Coldcard, reached over its own encrypted HID protocol. It has no coinjoin account of its own: it signs
/// from the wallet's ordinary accounts while running an HSM policy the user approves on the device, which
/// is what bounds a coinjoin session instead of a per-round confirmation.
/// </summary>
internal class ColdcardBackend : IHardwareWalletBackend
{
	public HardwareCoinJoinVendor Vendor => HardwareCoinJoinVendor.Coldcard;

	/// <summary>The key chain of each wallet, with the device session it holds.</summary>
	private readonly ConcurrentDictionary<KeyManager, ColdcardKeyChain> _keyChains = new();

	/// <summary>
	/// Installs the HSM policy the user approves on the device and returns the key chain that signs under it. A
	/// session the wallet held is given up first: it is only asked again once its budget is spent or its device
	/// stopped running the policy.
	/// </summary>
	public async Task<IKeyChain> AuthorizeCoinJoinAsync(KeyManager keyManager, string coordinatorIdentifier, CancellationToken cancellationToken)
	{
		ReleaseFor(keyManager);

		// Composed in one shared place, so the settings screen compares against exactly what would be sent.
		var policyJson = ComposePolicy(keyManager);

		var device = await Task.Run(() => OpenWalletDevice(keyManager), cancellationToken).ConfigureAwait(false);
		try
		{
			// Fail early with a clear message if this device's firmware can't run the policy (Mk3/older, Q).
			// Logged because which build is on the device decides which policy fields it understands, and
			// that is the first thing worth knowing when a policy is rejected.
			var deviceVersion = device.GetVersion();
			Logger.LogInfo($"Coldcard firmware: {deviceVersion.Replace('\n', ' ')}");
			ColdcardHsmPolicy.EnsureFirmwareSupportsPolicy(deviceVersion);

			// Remember which policy the device ended up running. HSM mode outlives this process, so on the
			// next start that recorded hash is what tells us the device is still enforcing what was agreed
			// to rather than something else.
			string? activeHash;
			try
			{
				activeHash = await Task.Run(
					() => device.StartHsm(policyJson, keyManager.ColdcardActivePolicyHash, keyManager.ColdcardApprovedPolicyFingerprint, cancellationToken),
					cancellationToken).ConfigureAwait(false);
			}
			catch (ColdcardException e) when (e.Message.Contains("Unknown item", StringComparison.Ordinal))
			{
				// Firmware predating one of the rules rejects the whole policy. Sending a weaker policy instead
				// would silently drop the limits the user set, so this is refused.
				throw new HardwareWalletException("This Coldcard's firmware does not understand the coinjoin policy. Install the coinjoin-fw-2 build.", e);
			}

			// Both are recorded together: the device hash says what it is enforcing, the fingerprint says
			// which of our settings produced it. Only the pair can tell a later session that the limits
			// were edited while the device stayed locked into the previous ones.
			if (activeHash is { Length: > 0 } && activeHash != keyManager.ColdcardActivePolicyHash)
			{
				keyManager.ColdcardActivePolicyHash = activeHash;
				keyManager.ColdcardApprovedPolicyFingerprint = ColdcardHsmPolicy.Fingerprint(policyJson);
				keyManager.ToFile();
			}

			var keyChain = new ColdcardKeyChain(device, keyManager);
			_keyChains[keyManager] = keyChain;
			return keyChain;
		}
		catch
		{
			device.Dispose();
			throw;
		}
	}

	public void ReleaseFor(KeyManager keyManager)
	{
		if (_keyChains.TryRemove(keyManager, out var keyChain))
		{
			keyChain.Dispose();
		}
	}

	/// <summary>
	/// Reads the wallet's BIP-86 account from its Coldcard and adds it, so taproot coins can be received and
	/// coinjoined. Only Edge firmware signs taproot, which <c>coinjoin-fw-2</c> is built on. Outside HSM mode
	/// only: a running policy does not share xpubs.
	/// </summary>
	internal static void AddTaprootAccount(KeyManager keyManager)
	{
		using var device = OpenWalletDevice(keyManager);
		var network = keyManager.GetNetwork();
		var accountKeyPath = KeyManager.GetAccountKeyPath(network, ScriptPubKeyType.TaprootBIP86);
		keyManager.AddTaprootAccount(accountKeyPath, device.GetXpub(accountKeyPath, network).ExtPubKey);
	}

	/// <summary>
	/// The policy this wallet's current settings would install. One method, so the settings screen and the
	/// authorization path can never disagree about what "the configured policy" is.
	/// </summary>
	internal static string ComposePolicy(KeyManager keyManager)
	{
		var accountPaths = new List<KeyPath> { keyManager.SegwitAccountKeyPath };
		if (keyManager.TaprootExtPubKey is not null)
		{
			accountPaths.Add(keyManager.TaprootAccountKeyPath);
		}

		return ColdcardHsmPolicy.Compose(accountPaths, new ColdcardHsmPolicy.ColdcardLimits(
			MaxSatsLeaving: keyManager.ColdcardMaxSatsLeaving,
			MaxTransactions: keyManager.CoinJoinDeviceMaxRounds,
			MaxTransactionsPerPeriod: keyManager.ColdcardMaxTransactionsPerPeriod,
			PeriodMinutes: keyManager.ColdcardPeriodMinutes,
			MinInputs: keyManager.ColdcardMinInputs > 0 ? keyManager.ColdcardMinInputs : null,
			// The same cap the client uses to pick rounds, enforced a second time by the device, which is
			// handed the transaction and can price it itself.
			MaxFeePerKvByte: ColdcardHsmPolicy.FeeRateToPerKvByte(keyManager.CoinJoinDeviceMaxMiningFeeRate)));
	}

	/// <summary>Opens the wallet's Coldcard, not just any Coldcard. The xfp arrives little-endian in the handshake.</summary>
	private static ColdcardDevice OpenWalletDevice(KeyManager keyManager)
	{
		var device = ColdcardDevice.Open();
		if (keyManager.MasterFingerprint is { } expectedFingerprint
			&& !BitConverter.GetBytes(device.MasterFingerprint).SequenceEqual(expectedFingerprint.ToBytes()))
		{
			device.Dispose();
			throw new ColdcardException("the connected device is not this wallet's Coldcard. Connect the right one and try again.");
		}

		return device;
	}
}
