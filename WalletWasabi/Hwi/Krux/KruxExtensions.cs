using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Hwi;

namespace WalletWasabi.Hwi.Krux;

public static class KruxExtensions
{
	/// <summary>A Krux signing coinjoins through the kruxd bridge. Like Coldcard it signs from the wallet's
	/// default accounts (isolation comes from the session policy approved on the device), so it is recorded
	/// at import via <see cref="KeyManager.CoinJoinVendor"/>.</summary>
	public static bool IsKruxCoinJoinWallet(this KeyManager keyManager) =>
		keyManager.GetCoinJoinVendor() == HardwareCoinJoinVendor.Krux;
}
