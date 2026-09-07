using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Hwi.Trezor;

namespace WalletWasabi.Tests.UnitTests.Hwi;

/// <summary>Watch-only wallets as importing a Trezor produces them, from the "all all all" seed Trezor's own tests use.</summary>
internal static class TestKeyManagers
{
	public static ExtKey MasterKey => new Mnemonic("all all all all all all all all all all all all").DeriveExtKey();

	/// <summary>A segwit account, plus the SLIP-25 coinjoin account as the taproot account when asked for.</summary>
	public static KeyManager WatchOnlyHardwareWallet(bool withCoinJoinAccount)
	{
		var masterKey = MasterKey;
		var coinJoinAccountKeyPath = TrezorDevice.GetCoinJoinAccountKeyPath(Network.Main);

		return KeyManager.CreateNewHardwareWalletWatchOnly(
			masterKey.Neuter().PubKey.GetHDFingerPrint(),
			masterKey.Derive(new KeyPath("84'/0'/0'")).Neuter(),
			withCoinJoinAccount ? masterKey.Derive(coinJoinAccountKeyPath).Neuter() : null,
			null,
			null,
			Network.Main,
			taprootAccountKeyPath: withCoinJoinAccount ? coinJoinAccountKeyPath : null);
	}

	/// <summary>The segwit and taproot accounts of a device that signs coinjoins from the wallet's ordinary accounts under its own policy (Coldcard, Passport, Krux).</summary>
	public static KeyManager PolicySignerWallet()
	{
		var masterKey = MasterKey;
		var taprootAccountKeyPath = new KeyPath("86'/0'/0'");

		return KeyManager.CreateNewHardwareWalletWatchOnly(
			masterKey.Neuter().PubKey.GetHDFingerPrint(),
			masterKey.Derive(new KeyPath("84'/0'/0'")).Neuter(),
			masterKey.Derive(taprootAccountKeyPath).Neuter(),
			null,
			null,
			Network.Main,
			taprootAccountKeyPath: taprootAccountKeyPath);
	}
}
