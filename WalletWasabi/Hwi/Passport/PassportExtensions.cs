using WalletWasabi.Blockchain.Keys;

namespace WalletWasabi.Hwi.Passport;

public static class PassportExtensions
{
	/// <summary>A Foundation Passport Prime set up for coinjoin. Like Coldcard it uses the default segwit
	/// account (isolation comes from the device's on-device session policy, not a separate SLIP-25 account),
	/// so it needs an explicit marker rather than being inferred from a key path.</summary>
	public static bool IsPassportCoinJoinWallet(this KeyManager keyManager) =>
		keyManager.IsHardwareWallet && keyManager.IsPassportCoinjoin;
}
