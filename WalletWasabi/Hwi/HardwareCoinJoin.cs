using WalletWasabi.Hwi.Models;

namespace WalletWasabi.Hwi;

/// <summary>
/// Which hardware vendor signs a coinjoin wallet's rounds. Vendors differ in how the user authorizes a batch
/// (Trezor: an on-device preauthorization bound to a SLIP-25 account; Coldcard: an HSM policy; Passport and
/// Krux: a session approved on the device) but not in how they sign, which is why signing goes through
/// <c>IKeyChain</c>. Persisted by number in the wallet file, so only ever append.
/// </summary>
public enum HardwareCoinJoinVendor
{
	None = 0,
	Trezor = 1,
	Coldcard = 2,
	Krux = 3,
	PassportPrime = 4,
}

public static class HardwareCoinJoin
{
	/// <summary>The vendor a device model belongs to, or <see cref="HardwareCoinJoinVendor.None"/> when that model cannot sign coinjoins.</summary>
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
			HardwareWalletModels.Krux
				or HardwareWalletModels.SabiSigner => HardwareCoinJoinVendor.Krux,
			_ => HardwareCoinJoinVendor.None,
		};

	/// <summary>Whether a detected device can act as a coinjoin remote signer, to offer it while importing.</summary>
	public static bool SupportsCoinJoin(this HardwareWalletModels model) =>
		model.VendorOf() != HardwareCoinJoinVendor.None;
}
