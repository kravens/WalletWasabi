using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Hwi;
using WalletWasabi.Hwi.Krux;
using WalletWasabi.Hwi.Models;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.Tests.UnitTests.Hwi;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.Wallets;
using WalletWasabi.Wallets.Backends;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Wallets;

/// <summary>What the Krux backend accepts from kruxd and what it refuses, without a device.</summary>
public class KruxBackendTests
{
	private static readonly ExtKey Seed = TestKeyManagers.MasterKey;
	private static readonly string Fingerprint = Seed.Neuter().PubKey.GetHDFingerPrint().ToString();

	private static string Info(int used = 0, int max = 5, bool? authorized = true, string device = "sabi") =>
		$$"""{"fingerprint":"{{Fingerprint}}","rounds_used":{{used}},"max_rounds":{{max}}{{(authorized is { } a ? $",\"authorized\":{a.ToString().ToLowerInvariant()}" : "")}},"device":"{{device}}","script_types":["p2tr"]}""";

	private static string Xpub(string path, ExtKey? seed = null) =>
		$$"""{"fingerprint":"{{(seed ?? Seed).Neuter().PubKey.GetHDFingerPrint()}}","xpub":"{{(seed ?? Seed).Derive(new KeyPath(path)).Neuter().ToString(Network.Main)}}"}""";

	private static KruxBackend Backend(FakeKruxd kruxd) =>
		new(Network.Main, () => new KruxClient("http://127.0.0.1:1", kruxd));

	private static Task<IKeyChain> AuthorizeAsync(KruxBackend backend, KeyManager keyManager, IKeyChain? existing = null) =>
		backend.AuthorizeCoinJoinAsync(keyManager, existing, "coordinator", 10, new FeeRate(5m), CancellationToken.None);

	[Fact]
	public async Task AnotherDevicesSessionIsRefusedAsync()
	{
		using var kruxd = new FakeKruxd().Answer("/info", Info().Replace(Fingerprint, "deadbeef"));
		using var backend = Backend(kruxd);
		var exception = await Assert.ThrowsAsync<HardwareWalletException>(() => AuthorizeAsync(backend, TestKeyManagers.PolicySignerWallet()));
		Assert.Contains("deadbeef", exception.Message);
	}

	[Fact]
	public async Task ASpentBudgetIsRefusedAsync()
	{
		using var kruxd = new FakeKruxd().Answer("/info", Info(used: 5, max: 5));
		using var backend = Backend(kruxd);
		var exception = await Assert.ThrowsAsync<HardwareWalletException>(() => AuthorizeAsync(backend, TestKeyManagers.PolicySignerWallet()));
		Assert.Contains("round budget", exception.Message);
	}

	[Fact]
	public async Task AnUnapprovedSessionIsRefusedAsync()
	{
		using var kruxd = new FakeKruxd().Answer("/info", Info(authorized: false));
		using var backend = Backend(kruxd);
		var exception = await Assert.ThrowsAsync<HardwareWalletException>(() => AuthorizeAsync(backend, TestKeyManagers.PolicySignerWallet()));
		Assert.Contains("approve", exception.Message);
	}

	[Fact]
	public async Task AnApprovedSessionYieldsAChainCappedByTheWalletAndTheDeviceAsync()
	{
		using var kruxd = new FakeKruxd().Answer("/info", Info(used: 2, max: 5));

		using var backend = Backend(kruxd);
		using var keyChain = (KruxKeyChain)await AuthorizeAsync(backend, TestKeyManagers.PolicySignerWallet());

		Assert.Equal(new FeeRate(5m), keyChain.MaxMiningFeeRate);
		Assert.False(keyChain.CanSign(ScriptType.P2WPKH)); // what the bridge reported
		Assert.False(keyChain.NeedsReauthorization);
	}

	[Fact]
	public async Task ALiveChainIsReusedWithoutAskingTheBridgeAsync()
	{
		using var kruxd = new FakeKruxd();
		using var existing = KruxKeyChainTests.Chain(kruxd, TestKeyManagers.PolicySignerWallet(), roundsRemaining: 3);
		using var backend = Backend(kruxd);

		var reused = await AuthorizeAsync(backend, TestKeyManagers.PolicySignerWallet(), existing);

		Assert.Same(existing, reused);
		Assert.Empty(kruxd.Requests);
		Assert.Equal(new FeeRate(5m), existing.MaxMiningFeeRate);
	}

	[Fact]
	public async Task ImportReadsBothAccountsAndPinsThemToTheSessionAsync()
	{
		using var kruxd = new FakeKruxd().Answer("/info", Info());
		kruxd.Replies["/xpub"] = request =>
			(System.Net.HttpStatusCode.OK, Xpub(request.RootElement.GetProperty("path")[0].GetUInt32() == 2147483732u ? "84'/0'/0'" : "86'/0'/0'"));
		var walletFilePath = Path.Combine(await Common.GetEmptyWorkDirAsync(), "wallet.json");
		var shown = new List<BitcoinAddress>();

		using var backend = Backend(kruxd);
		var keyManager = await backend.TryImportAsync(null, walletFilePath, enableCoinjoin: true, new Progress(shown), CancellationToken.None);

		Assert.NotNull(keyManager);
		Assert.True(File.Exists(walletFilePath));
		Assert.Equal(HardwareCoinJoinVendor.Krux, keyManager.GetCoinJoinVendor());
		Assert.True(HardwareWalletService.IsRemoteSigner(keyManager));
		Assert.Equal(Seed.Derive(new KeyPath("86'/0'/0'")).Neuter(), keyManager.TaprootExtPubKey);
		Assert.Equal(ScriptPubKeyType.TaprootBIP86, keyManager.DefaultReceiveScriptType);
		Assert.Equal(Seed.Derive(new KeyPath("86'/0'/0'/0/0")).Neuter().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Network.Main), Assert.Single(shown));
	}

	[Fact]
	public async Task AccountsOfAnotherSeedAreRefusedAsync()
	{
		using var kruxd = new FakeKruxd().Answer("/info", Info()).Answer("/xpub", Xpub("84'/0'/0'", new ExtKey()));
		var walletFilePath = Path.Combine(await Common.GetEmptyWorkDirAsync(), "wallet.json");

		using var backend = Backend(kruxd);
		await Assert.ThrowsAsync<HardwareWalletException>(() => backend.TryImportAsync(null, walletFilePath, true, null, CancellationToken.None));

		Assert.False(File.Exists(walletFilePath));
	}

	[Fact]
	public async Task ImportWithoutCoinjoinRecordsTheOptOutAsync()
	{
		using var kruxd = new FakeKruxd().Answer("/info", Info()).Answer("/xpub", Xpub("84'/0'/0'"));
		var walletFilePath = Path.Combine(await Common.GetEmptyWorkDirAsync(), "wallet.json");

		using var backend = Backend(kruxd);
		var keyManager = await backend.TryImportAsync(null, walletFilePath, enableCoinjoin: false, null, CancellationToken.None);

		Assert.NotNull(keyManager);
		Assert.True(keyManager.CoinJoinDisabled);
		Assert.Equal(HardwareCoinJoinVendor.None, keyManager.GetCoinJoinVendor());
	}

	[Fact]
	public async Task ASabiSignerIsListedWhenKruxdAnswersAndNothingWithoutABridgeAsync()
	{
		using var answering = new FakeKruxd().Answer("/info", Info());
		using var silent = new FakeKruxd { Unreachable = true };

		using var answeringBackend = Backend(answering);
		using var silentBackend = Backend(silent);
		var listed = await answeringBackend.TryDetectAsync(CancellationToken.None);
		var nothing = await silentBackend.TryDetectAsync(CancellationToken.None);

		Assert.NotNull(listed);
		Assert.Equal(HardwareWalletModels.SabiSigner, listed.Model);
		Assert.Equal(KruxClient.DefaultBridgeUri, listed.Path);
		Assert.Equal(Fingerprint, listed.Fingerprint.ToString());
		Assert.True(HardwareWalletService.CanSignCoinJoins(listed));
		Assert.Null(nothing);
		Assert.False(await silentBackend.IsTransportAvailableAsync(CancellationToken.None));
	}

	private sealed class Progress(List<BitcoinAddress> shown) : IProgress<BitcoinAddress>
	{
		public void Report(BitcoinAddress value) => shown.Add(value);
	}
}
