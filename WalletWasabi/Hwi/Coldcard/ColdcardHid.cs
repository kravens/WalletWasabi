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
		?? throw new InvalidOperationException("No Coldcard is connected. Connect (and Enable) USB on the device.");
}
