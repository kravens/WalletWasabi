using NBitcoin;

namespace WalletWasabi.Blockchain.Keys;

/// <summary>
/// SLIP-25 is the coinjoin account model: coinjoin funds live under a purpose of their own so that only
/// coinjoins spend them, and the account's xpub is a protected asset. Whether a wallet has such an account
/// is a statement about its key path, not about which vendor made it - vendors that sign coinjoins from the
/// wallet's default accounts under a device-side policy have no SLIP-25 account at all.
/// </summary>
public static class Slip25
{
	/// <summary>SLIP-25 purpose (10025'), the root of the coinjoin account.</summary>
	public const uint Purpose = 10025 | 0x80000000;

	/// <summary>A wallet whose taproot account is a SLIP-25 coinjoin account, kept apart from its other funds.</summary>
	public static bool UsesSlip25CoinJoinAccount(this KeyManager keyManager) =>
		keyManager.IsHardwareWallet && keyManager.TaprootAccountKeyPath.Indexes is [Purpose, ..];

	public static bool IsSlip25KeyPath(this KeyPath keyPath) =>
		keyPath.Indexes is [Purpose, ..];
}
