using NBitcoin;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
	public ColdcardBackend(Network network)
	{
		_network = network;
	}

	private readonly Network _network;

	public HardwareCoinJoinVendor Vendor => HardwareCoinJoinVendor.Coldcard;

	/// <summary>
	/// Installs the HSM policy the user approves on the device and returns the key chain that signs under it.
	/// </summary>
	public async Task<IKeyChain> AuthorizeCoinJoinAsync(
		KeyManager keyManager,
		IKeyChain? existingKeyChain,
		string coordinatorIdentifier,
		int maxRounds,
		FeeRate maxMiningFeeRate,
		CancellationToken cancellationToken)
	{
		if (existingKeyChain is ColdcardKeyChain existing)
		{
			// Reuse the HSM session only when the device is still reachable and the user's round budget
			// is not used up; otherwise rebuild it, which renews the budget with this fresh authorization.
			if (!existing.RoundsExhausted && IsDeviceAlive(existing))
			{
				return existing;
			}

			existing.Dispose();
		}

		// Four device-side guards, meaningful together: the self-transfer ratio (guards small amounts), an
		// absolute cap on value leaving (guards large ones), a transaction count (bounds the session) and a
		// per-period count (bounds a burst of rounds farmed for their fees). The sat/vByte cap has no HSM
		// equivalent and stays client-side, where CoinJoinTrackerFactory clamps the round selection.
		// Composed in one shared place, so the settings screen compares against exactly what would be sent.
		var accountPaths = new List<KeyPath> { keyManager.SegwitAccountKeyPath };
		if (keyManager.TaprootExtPubKey is not null)
		{
			accountPaths.Add(keyManager.TaprootAccountKeyPath);
		}
		var policyJson = ColdcardHsmPolicy.ComposeFor(keyManager);

		var device = await Task.Run(() => ColdcardDevice.Open(), cancellationToken).ConfigureAwait(false);
		try
		{
			// The wallet's device, not just any Coldcard. The xfp arrives little-endian in the handshake.
			if (keyManager.MasterFingerprint is { } expectedFingerprint
				&& !BitConverter.GetBytes(device.MasterFingerprint).SequenceEqual(expectedFingerprint.ToBytes()))
			{
				throw new ColdcardException(
					"the connected device is not this wallet's Coldcard. Connect the right one and try again.",
					"Wrong Coldcard connected");
			}

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
				// Firmware predating any of the newer rules rejects the whole policy over the unknown field,
				// so fall back to what every Mk4 understands rather than leaving the user unable to coinjoin.
				//
				// The floor goes back up on the way down. Relaxing it is only safe because max_sats_leaving
				// bounds the absolute loss beside it; with no absolute cap the ratio is the only value guard
				// there is, so carrying the relaxed number over would be a silent downgrade.
				Logger.LogWarning(
					$"This Coldcard's firmware does not understand part of the coinjoin policy ({e.Message}). "
					+ $"Falling back to a self-transfer floor of {ColdcardHsmPolicy.FloorWithoutAbsoluteCap}% with "
					+ "no absolute cap, no device-side transaction count, no rate limit and no minimum input "
					+ "count. The round budget and the round's size then rest on Wasabi alone, which cannot bind "
					+ "a host that has been taken over. Update the firmware to have the device enforce these.");

				var legacy = new ColdcardHsmPolicy.ColdcardLimits(
					MinSelfTransferPercent: ColdcardHsmPolicy.FloorWithoutAbsoluteCap,
					MaxSatsLeaving: null,
					MaxTransactions: null,
					MaxTransactionsPerPeriod: null,
					MinInputs: null);
				var legacyJson = ColdcardHsmPolicy.Compose(accountPaths, legacy);
				activeHash = await Task.Run(
					() => device.StartHsm(legacyJson, keyManager.ColdcardActivePolicyHash, keyManager.ColdcardApprovedPolicyFingerprint, cancellationToken),
					cancellationToken).ConfigureAwait(false);
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

			return new ColdcardKeyChain(device, keyManager, maxRounds);
		}
		catch
		{
			device.Dispose();
			throw;
		}
	}

	/// <summary>
	/// Asks the device what policy it ended up with. Best effort: failing to read it must not undo an
	/// authorization that already succeeded, so a device that will not answer reports nothing.
	/// </summary>
	public Task<DevicePolicyReport?> GetDevicePolicyAsync(IKeyChain keyChain, CancellationToken cancellationToken)
	{
		if (keyChain is not ColdcardKeyChain coldcard)
		{
			return Task.FromResult<DevicePolicyReport?>(null);
		}

		try
		{
			var status = coldcard.Device.GetHsmStatus();
			// A device with nothing to say about its policy is the same as a vendor that cannot: report nothing
			// rather than an empty summary the interface would then show as a blank panel.
			if (status.Summary is not { Length: > 0 } summary)
			{
				return Task.FromResult<DevicePolicyReport?>(null);
			}

			return Task.FromResult<DevicePolicyReport?>(new DevicePolicyReport(summary, status.PolicyHash ?? ""));
		}
		catch (Exception e)
		{
			Logger.LogDebug($"Could not read the device's active policy: {e.Message}");
			return Task.FromResult<DevicePolicyReport?>(null);
		}
	}

	/// <summary>A device that has been unplugged answers nothing; its session cannot be reused.</summary>
	private static bool IsDeviceAlive(ColdcardKeyChain keyChain)
	{
		try
		{
			keyChain.Device.GetVersion();
			return true;
		}
		catch
		{
			return false;
		}
	}

	public void Dispose()
	{
	}
}
