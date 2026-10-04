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
	/// <summary>The share of our own input value that must come back to us. Not the working limit - the fee rate
	/// cap is, since a ratio refuses honest rounds on small coins at a threshold that moves with the network - but
	/// the one guard that knows how much is at stake: 50% never touches a real round and still refuses catastrophe.
	/// Fixed, not a setting.</summary>
	public const double DefaultMinSelfTransferPercent = 50.0;

	/// <summary>Cap on our own value leaving in one transaction (our fee share plus any leak). Generous next to a
	/// real round, while bounding what a compromised host can take from a large wallet.</summary>
	public const long DefaultMaxSatsLeaving = 100_000;

	/// <summary>Transactions the device signs per period, so a coordinator proposing rounds back to back cannot
	/// farm the wallet's fee share until the total budget is gone.</summary>
	public const int DefaultMaxTransactionsPerPeriod = 6;

	public const int DefaultPeriodMinutes = 60;

	/// <summary>Fewest inputs the whole round may have, every participant's. The client's own minimum is worth
	/// nothing against a host that has been taken over; the device sees the transaction and counts for itself.
	/// A floor on how pointless a round may be, not an anonymity set.</summary>
	public const int DefaultMinInputs = 21;

	/// <summary>What the device enforces while it signs unattended.</summary>
	/// <param name="MinInputs">Null when the user turned the device-side floor off.</param>
	/// <param name="MaxFeePerKvByte">Sats per 1000 vbytes of our own inputs and outputs, see <see cref="FeeRateToPerKvByte"/>.</param>
	public record ColdcardLimits(
		long MaxSatsLeaving = DefaultMaxSatsLeaving,
		int MaxTransactions = 50,
		int MaxTransactionsPerPeriod = DefaultMaxTransactionsPerPeriod,
		int PeriodMinutes = DefaultPeriodMinutes,
		int? MinInputs = DefaultMinInputs,
		long MaxFeePerKvByte = 5_000);

	/// <summary>
	/// Converts the sat/vByte the user sets into the sats per 1000 vbytes the device rule takes, so a rate like
	/// 0.5 sat/vByte survives as an integer. Rounds up, so the device cap is never tighter than the one on screen.
	/// </summary>
	public static long FeeRateToPerKvByte(decimal satPerVByte) => (long)Math.Ceiling(satPerVByte * 1000m);

	public static string Compose(IEnumerable<KeyPath> accountPaths, ColdcardLimits limits)
	{
		// Whitelist both the receive (…/0/*) and change (…/1/*) branches of each account for signing and proofs.
		var paths = accountPaths
			.SelectMany(account => new[] { $"m/{account}/0/*", $"m/{account}/1/*" })
			.ToArray();

		var rule = new Dictionary<string, object>
		{
			["min_pct_self_transfer"] = DefaultMinSelfTransferPercent,
			["max_sats_leaving"] = limits.MaxSatsLeaving,
			["max_txn"] = limits.MaxTransactions,
			["max_txn_per_period"] = limits.MaxTransactionsPerPeriod,
		};
		if (limits.MinInputs is { } minInputs)
		{
			rule["min_inputs"] = minInputs;
		}
		rule["max_fee_per_kvbyte"] = limits.MaxFeePerKvByte;

		var policy = new Dictionary<string, object>
		{
			// Coinjoin PSBTs trip benign warnings (unusual shapes); the rules above are the real guard.
			["warnings_ok"] = true,
			["slip19_paths"] = paths,
			["rules"] = new[] { rule },
			["period"] = limits.PeriodMinutes,
		};

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
