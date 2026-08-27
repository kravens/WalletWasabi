using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using NBitcoin;

namespace WalletWasabi.Hwi.Coldcard;

/// <summary>
/// Builds the Coldcard HSM policy JSON that makes unattended coinjoin signing safe. The single guard that
/// matters for a coinjoin is <c>min_pct_self_transfer</c> (own outputs ÷ own inputs, foreign legs excluded):
/// a legitimate coinjoin keeps the wallet's value (ratio ≈ 100%), while any value leaking to a non-wallet
/// output drops the ratio and the device refuses to sign. That percentage also bounds the fee. Ownership
/// proofs are permitted only for the wallet's own account paths via <c>slip19_paths</c>.
/// </summary>
public static class ColdcardHsmPolicy
{
	/// <summary>The share of our own input value that must come back to us. No longer the working limit:
	/// <c>max_fee_per_kvbyte</c> is, because a ratio is the wrong shape for pricing a round. What a ratio
	/// permits scales with the amount being mixed, while mining fees scale the other way — a large share
	/// of a small coin, a trivial share of a big one — so the threshold where it starts refusing honest
	/// rounds moves with the network. At 5 sat/vByte a 95% floor refuses any coin under roughly 13k sats;
	/// at 50 sat/vByte it refuses anything under roughly 130k. That is what produced the 91.55%, 90.05%
	/// and 76.21% refusals observed on hardware. A feerate limit has no such term.
	///
	/// It is kept, at 50%, as the one guard that knows how much is at stake. The other two do not: a big
	/// byte count legitimately buys a big absolute loss, so a wallet made of many small coins can be bled
	/// a round at a time while every round is honestly priced per byte. 50% never touches a real round and
	/// still refuses catastrophe, which is the whole job it has left. Not user-configurable — a limit that
	/// only ever fires on disaster is not a dial anyone should be turning.</summary>
	public const double DefaultMinSelfTransferPercent = 50.0;

	/// <summary>The floor to use when the device cannot enforce an absolute cap. Without
	/// <c>max_sats_leaving</c> the ratio is the only guard there is, so it has to stay tight — relaxing it
	/// would be a downgrade, not a convenience.</summary>
	public const double FloorWithoutAbsoluteCap = 99.0;

	/// <summary>Absolute cap on our own value leaving in one transaction: our fee share plus any leak.
	/// 100k sats is generous next to a real round (a couple of our inputs and outputs at 100 sat/vByte is
	/// tens of thousands) while still bounding what a compromised host can take from a large wallet, where
	/// a percentage would allow far more.</summary>
	public const long DefaultMaxSatsLeaving = 100_000;

	/// <summary>How many transactions the device will sign per period. A total budget says nothing about
	/// how fast it is spent, and every round costs the wallet its fee share even when the coinjoin is
	/// honest, so a coordinator proposing rounds back to back can farm fees until the budget is gone.</summary>
	public const int DefaultMaxTransactionsPerPeriod = 6;

	/// <summary>Length of that period, in minutes.</summary>
	public const int DefaultPeriodMinutes = 60;

	/// <summary>Fewest inputs the whole round transaction may have — every participant's, not ours. The client
	/// already has a minimum input count, but the host is what picks the round, so that check is worth nothing
	/// against a host that has been taken over: it would simply join a round containing nobody but us and a
	/// coordinator that then learns the entire mapping. The device is handed the full round transaction, so it
	/// can count for itself. This is a floor on how pointless a round may be, <b>not</b> an anonymity set — a
	/// coordinator willing to register its own inputs can pad any round to any count and still know every link.
	/// 21 is comfortably below a real round (mainnet rounds run to the hundreds) and far above the degenerate
	/// ones this exists to refuse.</summary>
	public const int DefaultMinInputs = 21;

	/// <summary>What the device will enforce while it signs unattended. Grouped because the parts are only
	/// meaningful together: the ratio guards small amounts, the absolute cap guards large ones, the total
	/// bounds the session and the rate bounds the burst.</summary>
	/// <param name="MaxTransactions">Null for firmware predating <c>max_txn</c>; the whole policy would be
	/// rejected over the unknown field, and the client-side round budget still applies.</param>
	/// <param name="MaxSatsLeaving">Null for firmware predating <c>max_sats_leaving</c>. When it is null the
	/// ratio is the only value guard, so <see cref="FloorWithoutAbsoluteCap"/> should be used.</param>
	/// <param name="MaxTransactionsPerPeriod">Null for firmware predating <c>max_txn_per_period</c>.</param>
	/// <param name="MinInputs">Null for firmware predating <c>min_inputs</c>, and when the user has turned the
	/// floor off. Only the client-side minimum input count applies then, which the host can ignore.</param>
	/// <param name="MaxFeePerKvByte">Null for firmware predating <c>max_fee_per_kvbyte</c>. Sats per 1000
	/// vbytes of our own inputs and outputs — see <see cref="FeeRateToPerKvByte"/> for why the unit is not
	/// sat/vByte.</param>
	public record ColdcardLimits(
		double MinSelfTransferPercent = DefaultMinSelfTransferPercent,
		long? MaxSatsLeaving = DefaultMaxSatsLeaving,
		int? MaxTransactions = null,
		int? MaxTransactionsPerPeriod = DefaultMaxTransactionsPerPeriod,
		int PeriodMinutes = DefaultPeriodMinutes,
		int? MinInputs = DefaultMinInputs,
		long? MaxFeePerKvByte = null);

	/// <summary>
	/// Converts the sat/vByte the user sets into the sats-per-1000-vbytes the device rule takes. The device
	/// works in whole sats per 1000 vbytes so that a rate like 0.5 sat/vByte — which coordinators do set —
	/// survives as an integer instead of rounding to zero. Rounds up, so the cap the device enforces is never
	/// tighter than the number on screen.
	/// </summary>
	public static long FeeRateToPerKvByte(decimal satPerVByte) => (long)Math.Ceiling(satPerVByte * 1000m);

	public static string Compose(IEnumerable<KeyPath> accountPaths, ColdcardLimits limits)
	{
		// Whitelist both the receive (…/0/*) and change (…/1/*) branches of each account for signing and proofs.
		var paths = accountPaths
			.SelectMany(account => new[] { $"m/{account}/0/*", $"m/{account}/1/*" })
			.ToArray();

		// Built as a dictionary because every limit past the ratio is optional: firmware that predates a
		// field rejects the entire policy over the unknown key, so an absent limit must be absent, not zero.
		var rule = new Dictionary<string, object> { ["min_pct_self_transfer"] = limits.MinSelfTransferPercent };
		if (limits.MaxSatsLeaving is { } sats)
		{
			rule["max_sats_leaving"] = sats;
		}
		if (limits.MaxTransactions is { } max)
		{
			rule["max_txn"] = max;
		}
		if (limits.MaxTransactionsPerPeriod is { } rate)
		{
			rule["max_txn_per_period"] = rate;
		}
		if (limits.MinInputs is { } minInputs)
		{
			rule["min_inputs"] = minInputs;
		}
		if (limits.MaxFeePerKvByte is { } feeCap)
		{
			rule["max_fee_per_kvbyte"] = feeCap;
		}

		var policy = new Dictionary<string, object>
		{
			// Coinjoin PSBTs trip benign warnings (unusual shapes); the rules above are the real guard.
			["warnings_ok"] = true,
			["slip19_paths"] = paths,
			["rules"] = new[] { rule },
		};

		// The device requires a period whenever anything is measured per period.
		if (limits.MaxTransactionsPerPeriod is not null)
		{
			policy["period"] = limits.PeriodMinutes;
		}

		return JsonSerializer.Serialize(policy, new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict });
	}

	/// <summary>
	/// The policy this wallet's current settings would install. One method rather than two so the settings
	/// screen and the authorization path can never disagree about what "the configured policy" is — the whole
	/// point of the fingerprint is comparing them, which is worthless if they are composed differently.
	/// </summary>
	public static string ComposeFor(Blockchain.Keys.KeyManager keyManager)
	{
		var accountPaths = new List<KeyPath> { keyManager.SegwitAccountKeyPath };
		if (keyManager.TaprootExtPubKey is not null)
		{
			accountPaths.Add(keyManager.TaprootAccountKeyPath);
		}

		return Compose(accountPaths, new ColdcardLimits(
			// Fixed, not a setting: it is the sanity floor behind the feerate limit, not a knob.
			MinSelfTransferPercent: DefaultMinSelfTransferPercent,
			MaxSatsLeaving: keyManager.ColdcardMaxSatsLeaving,
			MaxTransactions: keyManager.CoinJoinDeviceMaxRounds,
			MaxTransactionsPerPeriod: keyManager.ColdcardMaxTransactionsPerPeriod,
			PeriodMinutes: keyManager.ColdcardPeriodMinutes,
			MinInputs: keyManager.ColdcardMinInputs > 0 ? keyManager.ColdcardMinInputs : null,
			// Same number the user already sets to skip expensive rounds, enforced a second time by the
			// device. The client-side check picks which round to join and so is worth nothing against a
			// host that has been taken over; the device is handed the transaction and can price it itself.
			MaxFeePerKvByte: FeeRateToPerKvByte(keyManager.CoinJoinDeviceMaxMiningFeeRate)));
	}

	/// <summary>
	/// Fingerprints the policy we composed, so a later session can tell whether the settings behind it have
	/// changed. Deliberately over our own JSON rather than the device's policy hash: the device's hash proves
	/// what it is enforcing, but says nothing about what the user has since asked for. Comparing the two is
	/// what catches a limit that was edited while the device was already locked into the previous one.
	/// </summary>
	public static string Fingerprint(string policyJson) =>
		Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(policyJson)))
			.ToLowerInvariant();

	/// <summary>
	/// Throws <see cref="NotSupportedException"/> for a Coldcard model that can't run this policy, turning an
	/// obscure device error into a clear message. Two models are permanent dead ends, confirmed on hardware:
	/// the <b>Q</b> disables the classic HSM command set entirely (it ships SSSP / Co-Sign instead — the device
	/// replies "HSM commands disabled"), and the <b>Mk3 and older</b> firmware line ended at 4.1.9, before the
	/// <c>min_pct_self_transfer</c> rule existed. The Mk4/Mk5 are the supported target and pass through. Pass
	/// the reply from <see cref="ColdcardDevice.GetVersion"/> — its lines include the model token
	/// (e.g. "mk4", "mk3", "q1").
	/// </summary>
	public static void EnsureFirmwareSupportsPolicy(string versionReply)
	{
		// Safe to substring-match model tokens: a git hash is hex, which never contains 'm', 'k' or 'q'.
		var v = (versionReply ?? "").ToLowerInvariant();

		if (v.Contains("q1"))
		{
			throw new NotSupportedException(
				"This Coldcard Q disables the HSM commands coinjoin needs — it uses SSSP / Co-Sign spending "
				+ "policies instead. A Coldcard Mk4 is required.");
		}

		if (v.Contains("mk1") || v.Contains("mk2") || v.Contains("mk3"))
		{
			throw new NotSupportedException(
				"This Coldcard (Mk3 or older) can't sign coinjoins: its firmware line ended at 4.1.9, before the "
				+ "'min_pct_self_transfer' HSM rule existed. A Coldcard Mk4 is required.");
		}
	}
}
