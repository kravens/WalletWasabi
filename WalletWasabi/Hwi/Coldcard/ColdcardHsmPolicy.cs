using System;
using System.Collections.Generic;
using System.Globalization;
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
	/// <summary>Default minimum self-transfer percentage. Tolerates ordinary coinjoin mining fees while
	/// rejecting any material value leak.</summary>
	public const double DefaultMinSelfTransferPercent = 90.0;

	public static string Compose(IEnumerable<KeyPath> accountPaths, double minSelfTransferPercent = DefaultMinSelfTransferPercent)
	{
		// Whitelist both the receive (…/0/*) and change (…/1/*) branches of each account for signing and proofs.
		var paths = accountPaths
			.SelectMany(account => new[] { $"m/{account}/0/*", $"m/{account}/1/*" })
			.ToArray();

		var policy = new
		{
			// Coinjoin PSBTs trip benign warnings (unusual shapes); the self-transfer rule is the real guard.
			warnings_ok = true,
			slip19_paths = paths,
			rules = new[]
			{
				new { min_pct_self_transfer = minSelfTransferPercent }
			}
		};

		return JsonSerializer.Serialize(policy, new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict });
	}

	/// <summary>
	/// Throws <see cref="NotSupportedException"/> when the connected Coldcard can't run this policy, turning
	/// the device's raw "Unknown item: min_pct_self_transfer" reject into a clear message. Both the
	/// self-transfer rule and the <c>slp9</c> ownership-proof command are Mk4-class firmware features: the Q
	/// dropped HSM mode entirely, and the Mk3/older firmware line ended at 4.1.9, before the rule existed.
	/// Pass the reply from <see cref="ColdcardDevice.GetVersion"/> — its lines include the model token
	/// (e.g. "mk4", "mk3", "q1"). A too-old Mk4 (very rare — the rule shipped years ago) falls through here and
	/// surfaces the device's own reject.
	/// </summary>
	public static void EnsureFirmwareSupportsPolicy(string versionReply)
	{
		// Safe to substring-match model tokens: a git hash is hex, which never contains 'm', 'k' or 'q'.
		var v = (versionReply ?? "").ToLowerInvariant();

		if (v.Contains("q1"))
		{
			throw new NotSupportedException(
				"This Coldcard Q has no HSM mode, so it cannot sign coinjoins unattended. A Coldcard Mk4 is required.");
		}

		if (v.Contains("mk1") || v.Contains("mk2") || v.Contains("mk3"))
		{
			throw new NotSupportedException(
				"This Coldcard (Mk3 or older) can't sign coinjoins: its firmware line ended at 4.1.9, before the "
				+ "'min_pct_self_transfer' HSM rule existed. A Coldcard Mk4 is required.");
		}
	}

	// Kept for symmetry with how the value is rendered elsewhere (invariant culture).
	internal static string Pct(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
