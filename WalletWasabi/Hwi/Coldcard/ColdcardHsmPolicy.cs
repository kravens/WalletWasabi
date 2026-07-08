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

	// Kept for symmetry with how the value is rendered elsewhere (invariant culture).
	internal static string Pct(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
