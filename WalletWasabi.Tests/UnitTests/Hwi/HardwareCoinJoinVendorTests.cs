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

/// <summary>Pins the model-to-vendor mapping and how the vendor is read back from a wallet file.</summary>
public class HardwareCoinJoinVendorTests
{
	[Theory]
	[InlineData(HardwareWalletModels.Trezor_T, HardwareCoinJoinVendor.Trezor)]
	[InlineData(HardwareWalletModels.Trezor_T_Simulator, HardwareCoinJoinVendor.Trezor)]
	[InlineData(HardwareWalletModels.Trezor_Safe_3, HardwareCoinJoinVendor.Trezor)]
	[InlineData(HardwareWalletModels.Trezor_Safe_5, HardwareCoinJoinVendor.Trezor)]
	[InlineData(HardwareWalletModels.Ledger_Nano_X, HardwareCoinJoinVendor.None)]
	[InlineData(HardwareWalletModels.Jade, HardwareCoinJoinVendor.None)]
	[InlineData(HardwareWalletModels.Trezor_1, HardwareCoinJoinVendor.None)]
	[InlineData(HardwareWalletModels.Unknown, HardwareCoinJoinVendor.None)]
	public void VendorOfMapsModel(HardwareWalletModels model, HardwareCoinJoinVendor expected) =>
		Assert.Equal(expected, model.VendorOf());

	[Fact]
	public async Task VendorIsReadFromTheWalletFileOrInferredForOlderOnesAsync()
	{
		var directory = await Common.GetEmptyWorkDirAsync();

		Assert.Equal(HardwareCoinJoinVendor.Trezor, Reload(TestKeyManagers.WatchOnlyHardwareWallet(withCoinJoinAccount: true), Path.Combine(directory, "stored.json")).CoinJoinVendor);

		// Written before the vendor was recorded: a SLIP-25 account means a Trezor, anything else no device.
		Assert.Equal(HardwareCoinJoinVendor.Trezor, Reload(TestKeyManagers.WatchOnlyHardwareWallet(withCoinJoinAccount: true), Path.Combine(directory, "trezor.json"), "CoinJoinVendor").CoinJoinVendor);
		Assert.Equal(HardwareCoinJoinVendor.None, Reload(TestKeyManagers.WatchOnlyHardwareWallet(withCoinJoinAccount: false), Path.Combine(directory, "plain.json"), "CoinJoinVendor").CoinJoinVendor);
	}

	/// <summary>Round-trips a wallet through its file, optionally without one key, as an older version wrote it.</summary>
	private static KeyManager Reload(KeyManager keyManager, string filePath, string? removeKey = null)
	{
		keyManager.SetFilePath(filePath);
		keyManager.ToFile();

		if (removeKey is not null)
		{
			var json = JsonNode.Parse(File.ReadAllText(filePath))!.AsObject();
			json.Remove(removeKey);
			File.WriteAllText(filePath, json.ToJsonString());
		}

		return KeyManager.FromFile(filePath);
	}
}
