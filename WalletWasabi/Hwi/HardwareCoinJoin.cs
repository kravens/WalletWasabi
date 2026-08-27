using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Hwi.Models;

namespace WalletWasabi.Hwi;

/// <summary>
/// Which hardware vendor acts as the remote signer for a coinjoin wallet. Vendors differ in how the user
/// authorizes a batch of rounds (Trezor: an on-device preauthorization bound to a SLIP-25 account; Coldcard:
/// an HSM policy) but not in how they sign, which is why signing itself goes through <c>IKeyChain</c>.
/// Persisted by number, so only ever append.
/// </summary>
public enum HardwareCoinJoinVendor
{
	None = 0,
	Trezor = 1,
	Coldcard = 2,
	Krux = 3,
	PassportPrime = 4,
}

/// <summary>
/// The vendor-neutral gates. Everything the coinjoin flow needs to know about "is this wallet signed by a
/// device, and which one" lives here, so adding a vendor is: a value in <see cref="HardwareCoinJoinVendor"/>,
/// an entry in <see cref="VendorOf"/>, a case in <c>Wallet.AuthorizeHardwareCoinJoinAsync</c>, and an
/// <c>IKeyChain</c> implementation. Behaviour that is really about the SLIP-25 account model (destinations,
/// account splitting, taproot-only coin selection) stays keyed on the account shape, not on the vendor.
/// </summary>
public static class HardwareCoinJoin
{
	/// <summary>The vendor backing this coinjoin wallet, or <see cref="HardwareCoinJoinVendor.None"/>.</summary>
	public static HardwareCoinJoinVendor GetCoinJoinVendor(this KeyManager keyManager)
	{
		if (!keyManager.IsHardwareWallet)
		{
			return HardwareCoinJoinVendor.None;
		}

		// Coinjoin is opt-in on a hardware wallet, so an explicit opt-out has to win over everything
		// below — including the SLIP-25 shape, which a Trezor keeps whether or not it is coinjoining.
		if (keyManager.CoinJoinDisabled)
		{
			return HardwareCoinJoinVendor.None;
		}

		if (keyManager.CoinJoinVendor != HardwareCoinJoinVendor.None)
		{
			return keyManager.CoinJoinVendor;
		}

		// Wallets imported before the vendor was recorded: a SLIP-25 taproot account means Trezor.
		return keyManager.UsesSlip25CoinJoinAccount() ? HardwareCoinJoinVendor.Trezor : HardwareCoinJoinVendor.None;
	}

	/// <summary>Any hardware wallet acting as a coinjoin remote signer. Used by the vendor-agnostic gates
	/// (authorization, music box, the reduced menu, round selection).</summary>
	public static bool IsHardwareCoinJoinWallet(this KeyManager keyManager) =>
		keyManager.GetCoinJoinVendor() != HardwareCoinJoinVendor.None;

	/// <summary>The vendor a connected device belongs to, or <see cref="HardwareCoinJoinVendor.None"/> when
	/// that model cannot act as a coinjoin signer.</summary>
	public static HardwareCoinJoinVendor VendorOf(this HardwareWalletModels model) =>
		model switch
		{
			HardwareWalletModels.Trezor_T
				or HardwareWalletModels.Trezor_T_Simulator
				or HardwareWalletModels.Trezor_Safe_3
				or HardwareWalletModels.Trezor_Safe_5 => HardwareCoinJoinVendor.Trezor,
			HardwareWalletModels.Coldcard
				or HardwareWalletModels.Coldcard_Simulator => HardwareCoinJoinVendor.Coldcard,
			HardwareWalletModels.Foundation_Passport => HardwareCoinJoinVendor.PassportPrime,
			// Krux is not in HWI's model list yet; add it here with its model.
			_ => HardwareCoinJoinVendor.None,
		};

	/// <summary>Device models that can act as a coinjoin remote signer (canonical predicate for all vendors).</summary>
	public static bool SupportsCoinJoin(this HardwareWalletModels model) =>
		model.VendorOf() != HardwareCoinJoinVendor.None;
}
