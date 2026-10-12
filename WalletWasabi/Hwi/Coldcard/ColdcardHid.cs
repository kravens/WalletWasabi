using WalletWasabi.Hwi.Usb;

namespace WalletWasabi.Hwi.Coldcard;

/// <summary>The USB identity of a Coldcard; it speaks a raw HID protocol (no bridge daemon, unlike Trezor).</summary>
public static class ColdcardUsb
{
	public const ushort VendorId = 0xd13e;
	public const ushort ProductId = 0xcc10;

	/// <summary>Opens the connected Coldcard. Throws if none is found.</summary>
	public static IUsbHid Open() =>
		UsbHid.TryOpen(VendorId, ProductId)
		?? throw new Wallets.HardwareWalletNotFoundException("No Coldcard is connected. Connect (and Enable) USB on the device.");

	/// <summary>Whether a Coldcard is attached right now. Enumeration only: nothing is opened or sent, so it is safe while the device is busy signing.</summary>
	public static bool IsConnected() => UsbHid.Enumerate(VendorId, ProductId).Count > 0;
}
