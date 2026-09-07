using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Hwi;
using WalletWasabi.Hwi.Models;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.Tests.UnitTests.Hwi;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.Wallets;
using WalletWasabi.Wallets.Backends;
using Xunit;

#pragma warning disable CA2000 // Dispose objects before losing scope - the fakes own nothing.

namespace WalletWasabi.Tests.UnitTests.Wallets;

/// <summary>What the Passport backend asks the device to approve, and what it refuses, without a device.</summary>
public class PassportBackendTests
{
	private static readonly HDFingerprint Fingerprint = TestKeyManagers.MasterKey.Neuter().PubKey.GetHDFingerPrint();

	private static PassportBackend Backend(FakePassportDevice? device, string? serial = "P1") =>
		new(Network.Main, () => serial is null ? [] : [serial], () => device!);

	private static Task<IKeyChain> AuthorizeAsync(PassportBackend backend, IKeyChain? existing = null) =>
		backend.AuthorizeCoinJoinAsync(TestKeyManagers.PolicySignerWallet(), existing, "coordinator", 10, new FeeRate(5m), CancellationToken.None);

	[Fact]
	public void TheFeeBudgetIsTheSessionTotalAndTheRoundsFitTheWire()
	{
		var policy = PassportBackend.ComposePolicy(Network.Main, "coordinator", 5, new FeeRate(10m));

		Assert.Equal(10ul * 1000 * 5, policy.FeeBudgetSats);
		Assert.Equal(5, policy.MaxRounds);
		Assert.Equal(12 * 3600u, policy.ValidForSeconds);
		Assert.Equal(ushort.MaxValue, PassportBackend.ComposePolicy(Network.Main, "c", 100_000, new FeeRate(1m)).MaxRounds);
		Assert.Equal(1, PassportBackend.ComposePolicy(Network.Main, "c", 0, new FeeRate(1m)).MaxRounds);
	}

	[Fact]
	public async Task AnApprovedSessionYieldsAChainBoundToTheDeviceAndTheWalletCapAsync()
	{
		var device = new FakePassportDevice();

		using var backend = Backend(device);
		using var keyChain = (PassportKeyChain)await AuthorizeAsync(backend);

		Assert.Equal(PassportBackend.ComposePolicy(Network.Main, "coordinator", 10, new FeeRate(5m)), device.ApprovedPolicy);
		Assert.Equal(new FeeRate(5m), keyChain.MaxMiningFeeRate);
		Assert.False(keyChain.NeedsReauthorization);
		Assert.Same(device, keyChain.Device);
	}

	[Fact]
	public async Task AnotherSeedsDeviceIsNotAskedToApproveAnythingAsync()
	{
		var device = new FakePassportDevice { Seed = new ExtKey() };

		using var backend = Backend(device);
		await Assert.ThrowsAsync<HardwareWalletException>(() => AuthorizeAsync(backend));

		Assert.DoesNotContain("authorize", device.Calls);
		Assert.True(device.Disposed);
	}

	[Fact]
	public async Task ALiveChainIsReusedAndASpentOrUnpluggedOneRevokedAndReplacedAsync()
	{
		var liveDevice = new FakePassportDevice();
		var unplugged = new FakePassportDevice { Alive = false };
		var replacement = new FakePassportDevice();
		using var live = new PassportKeyChain(liveDevice, FakePassportDevice.SessionToken.ToArray(), TestKeyManagers.PolicySignerWallet(), 3, DateTimeOffset.UtcNow.AddHours(1));
		var gone = new PassportKeyChain(unplugged, FakePassportDevice.SessionToken.ToArray(), TestKeyManagers.PolicySignerWallet(), 3, DateTimeOffset.UtcNow.AddHours(1));

		using var backend = Backend(replacement);
		var reused = await AuthorizeAsync(backend, live);
		using var replaced = (PassportKeyChain)await AuthorizeAsync(backend, gone);

		Assert.Same(live, reused);
		Assert.Empty(liveDevice.Calls);
		Assert.NotNull(unplugged.RevokedToken);
		Assert.True(unplugged.Disposed);
		Assert.Same(replacement, replaced.Device);
	}

	[Fact]
	public async Task ImportReadsBothAccountsFromTheDeviceAsync()
	{
		var device = new FakePassportDevice();
		var walletFilePath = Path.Combine(await Common.GetEmptyWorkDirAsync(), "wallet.json");
		var shown = new List<BitcoinAddress>();

		using var backend = Backend(device);
		var keyManager = await backend.TryImportAsync(null, walletFilePath, enableCoinjoin: true, new Progress(shown), CancellationToken.None);

		Assert.NotNull(keyManager);
		Assert.True(File.Exists(walletFilePath));
		Assert.Equal(HardwareCoinJoinVendor.PassportPrime, keyManager.GetCoinJoinVendor());
		Assert.Equal(Fingerprint, keyManager.MasterFingerprint);
		Assert.Equal(TestKeyManagers.MasterKey.Derive(new KeyPath("86'/0'/0'")).Neuter(), keyManager.TaprootExtPubKey);
		Assert.Equal(TestKeyManagers.MasterKey.Derive(new KeyPath("84'/0'/0'/0/0")).Neuter().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.Main), Assert.Single(shown));
		Assert.Equal(["xpub 84'/0'/0'", "xpub 86'/0'/0'"], device.Calls);
	}

	[Fact]
	public async Task AnotherDeviceThanTheWalletsIsNotImportedAsync()
	{
		var walletFilePath = Path.Combine(await Common.GetEmptyWorkDirAsync(), "wallet.json");

		using var backend = Backend(new FakePassportDevice());
		await Assert.ThrowsAsync<HardwareWalletException>(() => backend.TryImportAsync(new HDFingerprint(0xdeadbeef), walletFilePath, true, null, CancellationToken.None));

		Assert.False(File.Exists(walletFilePath));
	}

	[Fact]
	public async Task APassportIsListedWhenAttachedAndNothingOtherwiseAsync()
	{
		using var attached = Backend(new FakePassportDevice());
		using var none = Backend(null, serial: null);

		var listed = await attached.TryDetectAsync(CancellationToken.None);

		Assert.NotNull(listed);
		Assert.Equal(HardwareWalletModels.Foundation_Passport, listed.Model);
		Assert.Equal("P1", listed.Path);
		Assert.Equal(Fingerprint, listed.Fingerprint);
		Assert.True(HardwareWalletService.CanSignCoinJoins(listed));
		Assert.Null(await none.TryDetectAsync(CancellationToken.None));
		Assert.Null(await none.TryImportAsync(null, "unused", true, null, CancellationToken.None));
		Assert.False(await none.IsTransportAvailableAsync(CancellationToken.None));
	}

	private sealed class Progress(List<BitcoinAddress> shown) : IProgress<BitcoinAddress>
	{
		public void Report(BitcoinAddress value) => shown.Add(value);
	}
}
