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
	/// <summary>Default minimum self-transfer percentage. A compromised host can leak at most
	/// (100 − this)% of the wallet's inputs per signed transaction, so this is kept tight: a legitimate
	/// round keeps ~99.5%+ of the wallet's value (coordinator fee ≤ 0.3%, mining fee share well under
	/// that). The flip side is intentional: a round where our fees would exceed 1% (tiny coins at high
	/// fee rates, or value lost to failed output registrations) gets refused by the device. The number of
	/// rounds one authorization is good for is enforced client-side
	/// (<c>ColdcardKeyChain.RoundsExhausted</c>); a device-side round counter is a firmware follow-up.</summary>
	public const double DefaultMinSelfTransferPercent = 99.0;

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
