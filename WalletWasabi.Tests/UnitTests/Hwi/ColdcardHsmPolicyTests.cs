using System;
using System.Linq;
using System.Text.Json;
using NBitcoin;
using WalletWasabi.Hwi.Coldcard;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Hwi;

public class ColdcardHsmPolicyTests
{
	private static readonly KeyPath Segwit = new("84'/0'/0'");
	private static readonly KeyPath Taproot = new("86'/0'/0'");

	private static JsonElement Rule(string json)
	{
		using var doc = JsonDocument.Parse(json);
		return doc.RootElement.GetProperty("rules")[0].Clone();
	}

	[Fact]
	public void TheDefaultsGuardBothEndsOfTheScale()
	{
		// A ratio alone is the wrong shape: what it permits scales with the amount, while mining fees
		// scale the other way. The floor is only safe at 95 because the absolute cap sits beside it, and
		// the pair is what makes the relaxed ratio defensible — see FloorWithoutAbsoluteCap.
		Assert.Equal(95.0, ColdcardHsmPolicy.DefaultMinSelfTransferPercent);
		Assert.Equal(99.0, ColdcardHsmPolicy.FloorWithoutAbsoluteCap);
		Assert.Equal(100_000, ColdcardHsmPolicy.DefaultMaxSatsLeaving);
	}

	[Fact]
	public void ComposesCoinjoinPolicy()
	{
		var json = ColdcardHsmPolicy.Compose([Segwit, Taproot], new ColdcardHsmPolicy.ColdcardLimits());

		using var doc = JsonDocument.Parse(json);
		var root = doc.RootElement;

		Assert.True(root.GetProperty("warnings_ok").GetBoolean());
		Assert.Equal(95.0, root.GetProperty("rules")[0].GetProperty("min_pct_self_transfer").GetDouble());

		// Ownership proofs whitelisted for the receive and change branch of both accounts.
		var paths = root.GetProperty("slip19_paths").EnumerateArray().Select(x => x.GetString()).ToHashSet();
		Assert.Contains("m/84'/0'/0'/0/*", paths);
		Assert.Contains("m/84'/0'/0'/1/*", paths);
		Assert.Contains("m/86'/0'/0'/0/*", paths);
		Assert.Contains("m/86'/0'/0'/1/*", paths);
	}

	[Fact]
	public void EveryDeviceSideLimitReachesThePolicy()
	{
		// All six are enforced by the device. If any silently failed to serialise, the limit would exist
		// only in the UI while the device enforced nothing.
		var json = ColdcardHsmPolicy.Compose([Segwit], new ColdcardHsmPolicy.ColdcardLimits(
			MinSelfTransferPercent: 97.5,
			MaxSatsLeaving: 250_000,
			MaxTransactions: 50,
			MaxTransactionsPerPeriod: 4,
			PeriodMinutes: 30,
			MinInputs: 21,
			MaxFeePerKvByte: 5_000));

		var rule = Rule(json);
		Assert.Equal(97.5, rule.GetProperty("min_pct_self_transfer").GetDouble());
		Assert.Equal(250_000, rule.GetProperty("max_sats_leaving").GetInt64());
		Assert.Equal(50, rule.GetProperty("max_txn").GetInt32());
		Assert.Equal(4, rule.GetProperty("max_txn_per_period").GetInt32());
		Assert.Equal(21, rule.GetProperty("min_inputs").GetInt32());
		Assert.Equal(5_000, rule.GetProperty("max_fee_per_kvbyte").GetInt64());

		// The device refuses a policy that measures something per period without defining one.
		using var doc = JsonDocument.Parse(json);
		Assert.Equal(30, doc.RootElement.GetProperty("period").GetInt32());
	}

	[Fact]
	public void OmittedLimitsAreAbsentRatherThanZero()
	{
		// Firmware predating a field rejects the whole policy over the unknown key, so the fallback path
		// depends on absent meaning absent. A zero would also read as "allow nothing" if it were accepted.
		var json = ColdcardHsmPolicy.Compose([Segwit], new ColdcardHsmPolicy.ColdcardLimits(
			MinSelfTransferPercent: ColdcardHsmPolicy.FloorWithoutAbsoluteCap,
			MaxSatsLeaving: null,
			MaxTransactions: null,
			MaxTransactionsPerPeriod: null,
			MinInputs: null,
			MaxFeePerKvByte: null));

		var rule = Rule(json);
		Assert.Equal(99.0, rule.GetProperty("min_pct_self_transfer").GetDouble());
		Assert.False(rule.TryGetProperty("max_sats_leaving", out _));
		Assert.False(rule.TryGetProperty("max_txn", out _));
		Assert.False(rule.TryGetProperty("max_txn_per_period", out _));
		Assert.False(rule.TryGetProperty("min_inputs", out _));
		Assert.False(rule.TryGetProperty("max_fee_per_kvbyte", out _));

		using var doc = JsonDocument.Parse(json);
		Assert.False(doc.RootElement.TryGetProperty("period", out _));
	}

	[Theory]
	[InlineData(5, 5_000)]        // already high for a coinjoin
	[InlineData(0.5, 500)]        // the lowest a coordinator currently sets
	[InlineData(0.1, 100)]        // still relays
	[InlineData(1.5, 1_500)]
	public void TheFeeRateReachesTheDeviceInItsOwnUnit(decimal satPerVByte, long expected)
	{
		// The user types sat/vByte; the device rule is whole sats per 1000 vbytes. Getting this factor
		// wrong by 1000 would either refuse every round or enforce nothing, and both look like something
		// else from the outside. Sub-1 rates are the reason the device does not take sat/vByte directly:
		// 0.5 would truncate to 0 and refuse everything.
		Assert.Equal(expected, ColdcardHsmPolicy.FeeRateToPerKvByte(satPerVByte));
	}

	[Fact]
	public void TheFeeRateRoundsInTheUsersFavour()
	{
		// Rounding down would enforce a cap tighter than the number on screen, so a round priced exactly
		// at the user's limit would be refused by the device that was told to allow it.
		Assert.Equal(1_235, ColdcardHsmPolicy.FeeRateToPerKvByte(1.2341m));
	}

	[Fact]
	public void TheFingerprintChangesWithEveryLimit()
	{
		// The drift guard rests entirely on this: it is what tells a later session that the settings were
		// edited while the device stayed locked into the previous policy. A limit the fingerprint ignores
		// is a limit a user can tighten, be shown as active, and not actually get.
		var baseline = new ColdcardHsmPolicy.ColdcardLimits();
		var reference = Fingerprint(baseline);

		Assert.NotEqual(reference, Fingerprint(baseline with { MinSelfTransferPercent = 96.0 }));
		Assert.NotEqual(reference, Fingerprint(baseline with { MaxSatsLeaving = 50_000 }));
		Assert.NotEqual(reference, Fingerprint(baseline with { MaxTransactions = 10 }));
		Assert.NotEqual(reference, Fingerprint(baseline with { MaxTransactionsPerPeriod = 3 }));
		Assert.NotEqual(reference, Fingerprint(baseline with { PeriodMinutes = 120 }));
		Assert.NotEqual(reference, Fingerprint(baseline with { MinInputs = 50 }));
		Assert.NotEqual(reference, Fingerprint(baseline with { MaxFeePerKvByte = 5_000 }));

		// Turning a limit off has to register too, or disabling one would go unnoticed.
		Assert.NotEqual(reference, Fingerprint(baseline with { MinInputs = null }));

		static string Fingerprint(ColdcardHsmPolicy.ColdcardLimits limits) =>
			ColdcardHsmPolicy.Fingerprint(ColdcardHsmPolicy.Compose([Segwit], limits));
	}

	[Fact]
	public void TheFingerprintIsStableForUnchangedSettings()
	{
		// The other half: if it moved on its own, the guard would cry wolf on a policy that never changed
		// and send the user off to reboot a device enforcing exactly what they asked for.
		Assert.Equal(
			ColdcardHsmPolicy.Fingerprint(ColdcardHsmPolicy.Compose([Segwit, Taproot], new ColdcardHsmPolicy.ColdcardLimits())),
			ColdcardHsmPolicy.Fingerprint(ColdcardHsmPolicy.Compose([Segwit, Taproot], new ColdcardHsmPolicy.ColdcardLimits())));
	}

	[Fact]
	public void TheAccountPathsAreCoveredToo()
	{
		// A wallet whose taproot account appeared or vanished is running a different policy, since the
		// paths ownership proofs are allowed for came with it.
		Assert.NotEqual(
			ColdcardHsmPolicy.Fingerprint(ColdcardHsmPolicy.Compose([Segwit], new ColdcardHsmPolicy.ColdcardLimits())),
			ColdcardHsmPolicy.Fingerprint(ColdcardHsmPolicy.Compose([Segwit, Taproot], new ColdcardHsmPolicy.ColdcardLimits())));
	}

	[Fact]
	public void TurningTheInputFloorOffLeavesNothingForTheClientToCheck()
	{
		// 0 means "no device-side floor", and the coordinator-cap check keys on the same value. If it read
		// 0 as a floor rather than as off, every coordinator would trivially satisfy it and the check would
		// quietly do nothing - or worse, compare against a cap of 0 and refuse everything.
		Assert.Null(ColdcardHsmPolicy.Compose([Segwit], new ColdcardHsmPolicy.ColdcardLimits(MinInputs: null))
			is var json && JsonDocument.Parse(json).RootElement.GetProperty("rules")[0]
				.TryGetProperty("min_inputs", out var present) ? present.GetInt32() : (int?)null);
	}

	[Theory]
	[InlineData("4.1.9\nmk3\n")]                 // last Mk3 firmware: no min_pct_self_transfer, no newer build
	[InlineData("Coldcard MK2 bootloader\n")]
	[InlineData("1.4.1Q\nq1\n")]                  // Q: "HSM commands disabled" at firmware level (SSSP/Co-Sign only)
	public void RejectsUnsupportedModels(string versionReply)
	{
		Assert.Throws<NotSupportedException>(() => ColdcardHsmPolicy.EnsureFirmwareSupportsPolicy(versionReply));
	}

	[Theory]
	[InlineData("5.4.0\nmk4\n")]
	[InlineData("6.0.0\nmk5\n")]
	[InlineData("")]                              // unknown model: let the device decide, don't false-block
	public void AllowsMk4Mk5AndUnknown(string versionReply)
	{
		ColdcardHsmPolicy.EnsureFirmwareSupportsPolicy(versionReply); // no throw
	}
}
