using System;
using System.Linq;
using System.Text.Json;
using NBitcoin;
using WalletWasabi.Hwi.Coldcard;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Hwi;

public class ColdcardHsmPolicyTests
{
	[Fact]
	public void DefaultFloorAllowsAtMostOnePercentLeakPerTransaction()
	{
		// Security-relevant default: a compromised host may leak at most (100 - floor)% per signed tx.
		Assert.Equal(99.0, ColdcardHsmPolicy.DefaultMinSelfTransferPercent);
	}

	[Fact]
	public void ComposesCoinjoinPolicy()
	{
		var segwit = new KeyPath("84'/0'/0'");
		var taproot = new KeyPath("86'/0'/0'");

		var json = ColdcardHsmPolicy.Compose(new[] { segwit, taproot }, minSelfTransferPercent: 95.0);
		using var doc = JsonDocument.Parse(json);
		var root = doc.RootElement;

		// The value-leak guard: min_pct_self_transfer on the single rule.
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
	public void TheRoundBudgetIsPutOnTheDeviceWhenAskedFor()
	{
		// The floor alone caps a single transaction. max_txn is what bounds the total, and it has to be in
		// the policy the device approves, not only in the client-side counter a compromised host controls.
		var json = ColdcardHsmPolicy.Compose(new[] { new KeyPath("84'/0'/0'") }, maxTransactions: 50);

		using var doc = JsonDocument.Parse(json);
		Assert.Equal(50, doc.RootElement.GetProperty("rules")[0].GetProperty("max_txn").GetInt32());
	}

	[Fact]
	public void TheFloorIsSettable()
	{
		// Adjustable because 99 has little headroom: legitimate rounds were seen landing at 96.7-98.9%
		// when mining fees were a real fraction of the coins being mixed, and a device that refuses
		// everything with no way to tune it looks broken.
		var json = ColdcardHsmPolicy.Compose(new[] { new KeyPath("84'/0'/0'") }, minSelfTransferPercent: 97.5);

		using var doc = JsonDocument.Parse(json);
		Assert.Equal(97.5, doc.RootElement.GetProperty("rules")[0].GetProperty("min_pct_self_transfer").GetDouble());
	}

	[Fact]
	public void TheCountIsOmittedWhenNotAskedFor()
	{
		// Firmware predating the rule rejects the whole policy over an unknown field, so the field must be
		// absent rather than present-and-zero for the fallback path to work.
		var json = ColdcardHsmPolicy.Compose(new[] { new KeyPath("84'/0'/0'") });

		using var doc = JsonDocument.Parse(json);
		Assert.False(doc.RootElement.GetProperty("rules")[0].TryGetProperty("max_txn", out _));
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
