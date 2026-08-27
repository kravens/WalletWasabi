using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Hwi.Trezor;
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

	private static KeyManager Slip25Wallet()
	{
		var masterExtKey = new Mnemonic("all all all all all all all all all all all all").DeriveExtKey();
		var coinJoinAccountKeyPath = TrezorDevice.GetCoinJoinAccountKeyPath(Network.Main);
		return KeyManager.CreateNewHardwareWalletWatchOnly(
			masterExtKey.Neuter().PubKey.GetHDFingerPrint(),
			masterExtKey.Derive(new KeyPath("84'/0'/0'")).Neuter(),
			masterExtKey.Derive(coinJoinAccountKeyPath).Neuter(),
			null,
			null,
			Network.Main,
			taprootAccountKeyPath: coinJoinAccountKeyPath);
	}

	[Fact]
	public void RecordedVendorWinsOverTheAccountShape()
	{
		// A wallet can carry a SLIP-25 account and still be signed by another vendor, so what was recorded
		// at import has to beat what the key path implies.
		var keyManager = Slip25Wallet();
		Assert.Equal(HardwareCoinJoinVendor.Trezor, keyManager.GetCoinJoinVendor());

		keyManager.CoinJoinVendor = HardwareCoinJoinVendor.Coldcard;
		Assert.Equal(HardwareCoinJoinVendor.Coldcard, keyManager.GetCoinJoinVendor());
	}

	[Fact]
	public void OptingOutBeatsEverything()
	{
		// None cannot express "turned it off", because None also means "never set" and the SLIP-25 account
		// stays on the wallet either way. Without this the opt-out is silently ignored.
		var keyManager = Slip25Wallet();
		keyManager.CoinJoinDisabled = true;
		Assert.Equal(HardwareCoinJoinVendor.None, keyManager.GetCoinJoinVendor());
		Assert.False(keyManager.IsHardwareCoinJoinWallet());

		keyManager.CoinJoinVendor = HardwareCoinJoinVendor.Coldcard;
		Assert.Equal(HardwareCoinJoinVendor.None, keyManager.GetCoinJoinVendor());
	}

	[Fact]
	public void SoftwareWalletsHaveNoVendor()
	{
		var software = KeyManager.CreateNew(out _, "", Network.Main);
		Assert.Equal(HardwareCoinJoinVendor.None, software.GetCoinJoinVendor());
		Assert.False(software.UsesSlip25CoinJoinAccount());
	}
}
