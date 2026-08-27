using WalletWasabi.Blockchain.Keys;

namespace WalletWasabi.Hwi.Coldcard;

public static class ColdcardExtensions
{
	/// <summary>A Coldcard hardware wallet set up for coinjoin. Unlike Trezor it uses the default segwit and
	/// taproot accounts (isolation comes from the device's HSM policy, not a separate SLIP-25 account), so it
	/// is recorded at import rather than inferred from a key path. The vendor-agnostic gates live in
	/// <see cref="HardwareCoinJoin"/>; this stays for the genuinely Coldcard-specific branches.</summary>
	public static bool IsColdcardCoinJoinWallet(this KeyManager keyManager) =>
		keyManager.GetCoinJoinVendor() == HardwareCoinJoinVendor.Coldcard;
}
