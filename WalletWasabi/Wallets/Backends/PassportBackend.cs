using NBitcoin;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Hwi;
using WalletWasabi.Hwi.Passport;
using WalletWasabi.WabiSabi.Client;

namespace WalletWasabi.Wallets.Backends;

/// <summary>
/// Foundation Passport, reached over its own framing on USB HID. Like a Coldcard it signs from the wallet's
/// ordinary accounts, and like a Trezor the user approves a batch up front - here as a session policy the
/// device holds for a fixed time.
/// </summary>
internal class PassportBackend : IHardwareWalletBackend
{
	public PassportBackend(Network network)
	{
		_network = network;
	}

	private readonly Network _network;

	public HardwareCoinJoinVendor Vendor => HardwareCoinJoinVendor.PassportPrime;

	public async Task<IKeyChain> AuthorizeCoinJoinAsync(
		KeyManager keyManager,
		IKeyChain? existingKeyChain,
		string coordinatorIdentifier,
		int maxRounds,
		FeeRate maxMiningFeeRate,
		CancellationToken cancellationToken)
	{
		if (existingKeyChain is { NeedsReauthorization: false } and PassportKeyChain authorized)
		{
			// Already inside an authorized session for this run.
			return authorized;
		}

		// Not a session we can carry on with, so it is ours to close before opening another.
		(existingKeyChain as IDisposable)?.Dispose();

		// Passport enforces one policy per authorized session: the default segwit account, this coordinator,
		// a per-round fee-contribution cap, self-spend outputs and a round budget. The user reviews and
		// approves it once on the device; afterwards ownership proofs and signatures are produced unattended.
		var policy = new CoinjoinPolicy
		{
			Network = _network,
			Account = 0,
			CoordinatorIdentifier = coordinatorIdentifier,
			MaxFeeContributionSats = (ulong)MaxFeeContributionPerRound(maxMiningFeeRate).Satoshi,
			MaxRounds = (ushort)Math.Clamp(maxRounds, 1, ushort.MaxValue),
			ValidForSeconds = (uint)TimeSpan.FromHours(12).TotalSeconds,
		};

		var device = await Task.Run(() => PassportDevice.Open(), cancellationToken).ConfigureAwait(false);
		try
		{
			var sessionId = await Task.Run(() => device.AuthorizeCoinJoin(policy), cancellationToken).ConfigureAwait(false);
			return new PassportKeyChain(device, sessionId, keyManager);
		}
		catch
		{
			device.Dispose();
			throw;
		}
	}

	/// <summary>
	/// Turns the coinjoin max mining fee rate into a per-round sats cap for the session policy. A wallet
	/// registers few inputs per round, so bound the per-round input vsize generously: the cap should catch
	/// genuinely excessive fees without rejecting ordinary rounds. The device treats it as the most it may
	/// lose per round, mining and coordination fee share together.
	/// </summary>
	private static Money MaxFeeContributionPerRound(FeeRate maxMiningFeeRate)
	{
		// ponytail: flat per-round input-vsize budget. Tighten to the actual registered input count if rounds
		// with many inputs need a snugger cap.
		const int MaxRegisteredInputVsizePerRound = 4 * 110; // ~4 P2WPKH inputs
		return maxMiningFeeRate.GetFee(MaxRegisteredInputVsizePerRound);
	}

	public void Dispose()
	{
	}
}
