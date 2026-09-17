using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Hwi.Krux;
using WalletWasabi.Wallets;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Hwi;

/// <summary>What the wallet sends kruxd and how it reads the answers, against the bridge's documented HTTP shapes.</summary>
public class KruxClientTests
{
	private const string Fingerprint = "5c9e228d";

	private static KruxClient Client(FakeKruxd kruxd, TimeSpan? infoTimeout = null) =>
		new("http://127.0.0.1:1", kruxd, infoTimeout);

	[Fact]
	public async Task InfoParsesBudgetAuthorizationDeviceAndScriptTypesAsync()
	{
		using var kruxd = new FakeKruxd().Answer("/info", """{"fingerprint":"5c9e228d","rounds_used":2,"max_rounds":5,"authorized":true,"device":"sabi","script_types":["p2tr"]}""");
		using var client = Client(kruxd);

		var info = await client.GetInfoAsync(CancellationToken.None);

		Assert.Equal(new HDFingerprint(Convert.FromHexString(Fingerprint)), info.Fingerprint);
		Assert.Equal((2, 5), (info.RoundsUsed, info.MaxRounds));
		Assert.True(info.Authorized);
		Assert.Equal("sabi", info.Device);
		Assert.Equal([ScriptType.Taproot], info.ScriptTypes);
	}

	[Fact]
	public async Task AnOlderBridgeThatSaysLessMeansAKruxSigningBothTypesAsync()
	{
		using var kruxd = new FakeKruxd().Answer("/info", """{"fingerprint":"5c9e228d","rounds_used":0,"max_rounds":0}""");
		using var client = Client(kruxd);

		var info = await client.GetInfoAsync(CancellationToken.None);

		Assert.Null(info.Authorized);
		Assert.Equal("krux", info.Device);
		Assert.Equal([ScriptType.P2WPKH, ScriptType.Taproot], info.ScriptTypes);
	}

	[Fact]
	public async Task ProofRequestCarriesScriptTypeRawPathAndLowercaseHexCommitmentAsync()
	{
		using var kruxd = new FakeKruxd().Answer("/proof", """{"proof":"0102"}""");
		using var client = Client(kruxd);

		var proof = await client.GetOwnershipProofAsync(new KeyPath("86'/0'/0'/0/5"), ScriptPubKeyType.TaprootBIP86, [0xAB, 0xCD], CancellationToken.None);

		var (path, body) = Assert.Single(kruxd.Requests);
		Assert.Equal("/proof", path);
		Assert.Equal("p2tr", body.RootElement.GetProperty("script_type").GetString());
		Assert.Equal([2147483734u, 2147483648u, 2147483648u, 0u, 5u], body.RootElement.GetProperty("path").EnumerateArray().Select(i => i.GetUInt32()));
		Assert.Equal("abcd", body.RootElement.GetProperty("commitment").GetString());
		Assert.Equal([0x01, 0x02], proof);
	}

	[Fact]
	public async Task SignRequestCarriesTheBase64PsbtAndTheReplyIsParsedAsync()
	{
		var transaction = Transaction.Create(Network.Main);
		transaction.Inputs.Add(new OutPoint(uint256.One, 0));
		var psbt = PSBT.FromTransaction(transaction, Network.Main);
		using var kruxd = new FakeKruxd().Answer("/sign", $$"""{"psbt":"{{psbt.ToBase64()}}"}""");
		using var client = Client(kruxd);

		var signed = await client.SignCoinJoinAsync(psbt, Network.Main, CancellationToken.None);

		var (_, body) = Assert.Single(kruxd.Requests);
		Assert.Equal(psbt.ToBase64(), body.RootElement.GetProperty("psbt").GetString());
		Assert.Equal(psbt.ToBase64(), signed.ToBase64());
	}

	[Fact]
	public async Task XpubRequestCarriesThePathAndAnyPrefixIsParsedAsync()
	{
		var account = TestKeyManagers.MasterKey.Derive(new KeyPath("84'/0'/0'")).Neuter();
		using var kruxd = new FakeKruxd().Answer("/xpub", $$"""{"fingerprint":"{{Fingerprint}}","xpub":"{{account.ToString(Network.TestNet)}}"}""");
		using var client = Client(kruxd);

		var (fingerprint, extPubKey) = await client.GetXpubAsync(new KeyPath("84'/0'/0'"), CancellationToken.None);

		var (_, body) = Assert.Single(kruxd.Requests);
		Assert.Equal([2147483732u, 2147483648u, 2147483648u], body.RootElement.GetProperty("path").EnumerateArray().Select(i => i.GetUInt32()));
		Assert.Equal(new HDFingerprint(Convert.FromHexString(Fingerprint)), fingerprint);
		Assert.Equal(account, extPubKey); // a tpub carries the same key as the xpub
	}

	[Fact]
	public async Task ADeviceRefusalIsAHardwareWalletErrorWithTheDevicesReasonAsync()
	{
		using var kruxd = new FakeKruxd().Answer("/proof", """{"error":"path outside the authorized account"}""", HttpStatusCode.BadRequest);
		using var client = Client(kruxd);

		var exception = await Assert.ThrowsAsync<KruxException>(() => client.GetOwnershipProofAsync(new KeyPath("84'/0'/1'/0/0"), ScriptPubKeyType.Segwit, [], CancellationToken.None));

		Assert.Contains("authorized account", exception.Message);
		Assert.IsAssignableFrom<HardwareWalletException>(exception);
	}

	[Fact]
	public async Task NothingListeningIsReportedAsAMissingTransportAsync()
	{
		using var kruxd = new FakeKruxd { Unreachable = true };
		using var client = Client(kruxd);

		await Assert.ThrowsAsync<HardwareWalletTransportNotFoundException>(() => client.GetInfoAsync(CancellationToken.None));
	}

	[Fact]
	public async Task AStalledBridgeIsGivenUpOnAsync()
	{
		using var kruxd = new FakeKruxd { Stalled = true };
		using var client = Client(kruxd, infoTimeout: TimeSpan.FromMilliseconds(100));

		var exception = await Assert.ThrowsAsync<KruxException>(() => client.GetInfoAsync(CancellationToken.None));

		Assert.Contains("did not answer", exception.Message);
	}
}
