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
	/// <summary>The share of our own input value that must come back to us. On its own a ratio is the
	/// wrong shape for this job: what it permits scales with the amount being mixed, while mining fees
	/// scale the other way — a large share of a small coin, a trivial share of a big one. A percentage
	/// tight enough to protect large amounts therefore refuses ordinary rounds on small ones, which is
	/// what 99% did on hardware (legitimate rounds landing at 96.7–98.9%). It is safe to relax to 95%
	/// only because <see cref="DefaultMaxSatsLeaving"/> bounds the absolute loss alongside it.</summary>
	public const double DefaultMinSelfTransferPercent = 95.0;

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
	public record ColdcardLimits(
		double MinSelfTransferPercent = DefaultMinSelfTransferPercent,
		long? MaxSatsLeaving = DefaultMaxSatsLeaving,
		int? MaxTransactions = null,
		int? MaxTransactionsPerPeriod = DefaultMaxTransactionsPerPeriod,
		int PeriodMinutes = DefaultPeriodMinutes,
		int? MinInputs = DefaultMinInputs);

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
