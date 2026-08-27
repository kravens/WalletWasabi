using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Hwi;

namespace WalletWasabi.Hwi.Passport;

public static class PassportExtensions
{
	/// <summary>A Foundation Passport Prime set up for coinjoin. Like Coldcard it uses the default segwit
	/// account (isolation comes from the device's on-device session policy, not a separate SLIP-25 account),
	/// so it is recorded at import via <see cref="KeyManager.CoinJoinVendor"/>.</summary>
	public static bool IsPassportCoinJoinWallet(this KeyManager keyManager) =>
		keyManager.GetCoinJoinVendor() == HardwareCoinJoinVendor.PassportPrime;
}
