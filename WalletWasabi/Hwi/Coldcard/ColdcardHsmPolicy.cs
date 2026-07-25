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
	/// <summary>Default minimum self-transfer percentage. A legitimate round keeps ~99.5%+ of the wallet's
	/// value (coordinator fee ≤ 0.3%, mining fee share well under that), so 99% is tight with a little
	/// headroom. The flip side is intentional: a round where our fees would exceed 1% (tiny coins at high
	/// fee rates, or value lost to failed output registrations) gets refused by the device.
	/// <para>
	/// On its own this bounds the leak per <em>signed transaction</em>, not in total: a host that had been
	/// taken over could keep presenting fresh 99%-self-transfer coinjoins and skim 1% each time. That is why
	/// the policy also carries <c>max_txn</c>, so the device counts the transactions it signs instead of
	/// trusting <c>ColdcardKeyChain.RoundsExhausted</c>, which lives in the very process such an attacker
	/// controls. The policy's existing velocity limits cannot serve here: <c>per_period</c> and
	/// <c>max_amount</c> are both measured against non-change outputs, which in a coinjoin are the other
	/// participants' outputs, so any limit tight enough to matter would refuse honest rounds.
	/// </para></summary>
	public const double DefaultMinSelfTransferPercent = 99.0;

	/// <param name="maxTransactions">How many transactions this authorization is good for, enforced by the
	/// device. Pass <c>null</c> for a Coldcard whose firmware predates the <c>max_txn</c> rule — it would
	/// reject the whole policy over an unknown field, and the client-side budget still applies.</param>
	public static string Compose(
		IEnumerable<KeyPath> accountPaths,
		int? maxTransactions = null,
		double minSelfTransferPercent = DefaultMinSelfTransferPercent)
	{
		// Whitelist both the receive (…/0/*) and change (…/1/*) branches of each account for signing and proofs.
		var paths = accountPaths
			.SelectMany(account => new[] { $"m/{account}/0/*", $"m/{account}/1/*" })
			.ToArray();

		// The two limits are meant to be read together: the floor caps what any one transaction can move,
		// max_txn caps how many there can be, so the total a compromised host could move is bounded. Without
		// the count the floor is a per-transaction limit only, and the round budget that would bound the
		// total sits in the host that is being assumed compromised.
		var rule = maxTransactions is { } max
			? (object)new { min_pct_self_transfer = minSelfTransferPercent, max_txn = max }
			: new { min_pct_self_transfer = minSelfTransferPercent };

		var policy = new
		{
			// Coinjoin PSBTs trip benign warnings (unusual shapes); the self-transfer rule is the real guard.
			warnings_ok = true,
			slip19_paths = paths,
			rules = new[] { rule }
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
