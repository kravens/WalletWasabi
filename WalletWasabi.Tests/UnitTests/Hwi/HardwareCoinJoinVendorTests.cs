using WalletWasabi.Tests.Helpers;
using System.Threading.Tasks;
using System.Text.Json.Nodes;
using System.IO;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Hwi;
using WalletWasabi.Hwi.Models;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Hwi;

/// <summary>
/// Pins the model-to-vendor mapping. The bug this guards against: a Trezor-only code path (reading a
/// SLIP-25 account over the Trezor bridge) was gated on the vendor-neutral "can this device coinjoin"
/// predicate, so importing a Coldcard with coinjoin enabled walked into the bridge and failed with
/// "No Trezor device found". Vendor-neutral predicates must never stand in for "is a Trezor".
/// </summary>
public class HardwareCoinJoinVendorTests
{
	[Theory]
	[InlineData(HardwareWalletModels.Trezor_T, HardwareCoinJoinVendor.Trezor)]
	[InlineData(HardwareWalletModels.Trezor_T_Simulator, HardwareCoinJoinVendor.Trezor)]
	[InlineData(HardwareWalletModels.Trezor_Safe_3, HardwareCoinJoinVendor.Trezor)]
	[InlineData(HardwareWalletModels.Trezor_Safe_5, HardwareCoinJoinVendor.Trezor)]
	[InlineData(HardwareWalletModels.Coldcard, HardwareCoinJoinVendor.Coldcard)]
	[InlineData(HardwareWalletModels.Coldcard_Simulator, HardwareCoinJoinVendor.Coldcard)]
	[InlineData(HardwareWalletModels.Foundation_Passport, HardwareCoinJoinVendor.PassportPrime)]
	[InlineData(HardwareWalletModels.Krux, HardwareCoinJoinVendor.Krux)]
	[InlineData(HardwareWalletModels.SabiSigner, HardwareCoinJoinVendor.Krux)]
	[InlineData(HardwareWalletModels.Ledger_Nano_X, HardwareCoinJoinVendor.None)]
	[InlineData(HardwareWalletModels.Jade, HardwareCoinJoinVendor.None)]
	[InlineData(HardwareWalletModels.Trezor_1, HardwareCoinJoinVendor.None)]
	[InlineData(HardwareWalletModels.Unknown, HardwareCoinJoinVendor.None)]
	public void VendorOfMapsModel(HardwareWalletModels model, HardwareCoinJoinVendor expected) =>
		Assert.Equal(expected, model.VendorOf());

	[Fact]
	public void SupportsCoinJoinIsNotTrezorOnly()
	{
		// The distinction the import path got wrong: a Coldcard supports coinjoin but is not a Trezor,
		// so SupportsCoinJoin() must not be used to decide whether to talk to the Trezor bridge.
		Assert.True(HardwareWalletModels.Coldcard.SupportsCoinJoin());
		Assert.NotEqual(HardwareCoinJoinVendor.Trezor, HardwareWalletModels.Coldcard.VendorOf());

		Assert.True(HardwareWalletModels.Trezor_T.SupportsCoinJoin());
		Assert.Equal(HardwareCoinJoinVendor.Trezor, HardwareWalletModels.Trezor_T.VendorOf());
	}

	[Fact]
	public async Task VendorIsReadFromTheWalletFileOrInferredForOlderOnesAsync()
	{
		var directory = await Common.GetEmptyWorkDirAsync();

		// Written before the vendor was recorded: a SLIP-25 account means a Trezor, the Coldcard flag its old name.
		Assert.Equal(HardwareCoinJoinVendor.Trezor, Reload(TestKeyManagers.WatchOnlyHardwareWallet(withCoinJoinAccount: true), Path.Combine(directory, "trezor.json"), "CoinJoinVendor").CoinJoinVendor);
		Assert.Equal(HardwareCoinJoinVendor.Coldcard, Reload(TestKeyManagers.PolicySignerWallet(), Path.Combine(directory, "coldcard.json"), "CoinJoinVendor", ("IsColdcardCoinjoin", true)).CoinJoinVendor);
		Assert.Equal(HardwareCoinJoinVendor.None, Reload(TestKeyManagers.PolicySignerWallet(), Path.Combine(directory, "plain.json"), "CoinJoinVendor").CoinJoinVendor);

		// Previews 1-4 wrote the vendor as None for a Trezor (its SLIP-25 account said so) and the opt-out as its own flag.
		var preview4Trezor = TestKeyManagers.WatchOnlyHardwareWallet(withCoinJoinAccount: true);
		preview4Trezor.CoinJoinVendor = HardwareCoinJoinVendor.None;
		Assert.Equal(HardwareCoinJoinVendor.Trezor, Reload(preview4Trezor, Path.Combine(directory, "preview4-trezor.json"), null, ("CoinJoinDisabled", false)).CoinJoinVendor);
		var optedOut = Reload(preview4Trezor, Path.Combine(directory, "optedout.json"), null, ("CoinJoinDisabled", true));
		Assert.True(optedOut.HasCoinJoinAccount);
		Assert.False(optedOut.IsCoinJoinSignedByDevice);
	}

	/// <summary>Round-trips a wallet through its file, with a key removed and others added as older versions wrote them.</summary>
	private static KeyManager Reload(KeyManager keyManager, string filePath, string? removeKey = null, params (string Key, bool Value)[] add)
	{
		keyManager.SetFilePath(filePath);
		keyManager.ToFile();

		var json = JsonNode.Parse(File.ReadAllText(filePath))!.AsObject();
		if (removeKey is not null)
		{
			json.Remove(removeKey);
		}
		foreach (var (key, value) in add)
		{
			json[key] = value;
		}
		File.WriteAllText(filePath, json.ToJsonString());

		return KeyManager.FromFile(filePath);
	}
}
