using System.Linq;
using System.Text.Json;
using NBitcoin;
using WalletWasabi.Hwi.Coldcard;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Hwi;

public class ColdcardHsmPolicyTests
{
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
}
