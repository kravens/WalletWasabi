using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Hwi.Models;
using WalletWasabi.Hwi.Passport;
using WalletWasabi.Hwi.Trezor;

namespace WalletWasabi.Hwi.Coldcard;

public static class ColdcardExtensions
{
	/// <summary>A Coldcard hardware wallet set up for coinjoin. Unlike Trezor it uses the default segwit and
	/// taproot accounts (isolation comes from the device's HSM policy, not a separate SLIP-25 account), so it
	/// is marked explicitly rather than inferred from a key path.</summary>
	public static bool IsColdcardCoinJoinWallet(this KeyManager keyManager) =>
		keyManager.IsHardwareWallet && keyManager.IsColdcardCoinjoin;

	/// <summary>Any hardware wallet acting as a coinjoin remote signer (Trezor, Coldcard or Passport). Used by
	/// the vendor-agnostic gates (coinjoin authorization, music box, the reduced menu); vendor-specific behavior
	/// (SLIP-25 account rules for Trezor) keeps using the per-vendor predicate.</summary>
	public static bool IsHardwareCoinJoinWallet(this KeyManager keyManager) =>
		keyManager.IsTrezorCoinJoinWallet() || keyManager.IsColdcardCoinJoinWallet() || keyManager.IsPassportCoinJoinWallet();

	/// <summary>Device models that can act as a coinjoin remote signer (canonical predicate for all vendors).</summary>
	public static bool SupportsCoinJoin(this HardwareWalletModels model) =>
		model is HardwareWalletModels.Trezor_T
			or HardwareWalletModels.Trezor_T_Simulator
			or HardwareWalletModels.Trezor_Safe_3
			or HardwareWalletModels.Trezor_Safe_5
			or HardwareWalletModels.Coldcard
			or HardwareWalletModels.Coldcard_Simulator
			or HardwareWalletModels.Foundation_Passport;
}
