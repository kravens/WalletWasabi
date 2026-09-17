using WalletWasabi.Hwi.Usb;

namespace WalletWasabi.Hwi.Coldcard;

/// <summary>The USB identity of a Coldcard; it speaks a raw HID protocol (no bridge daemon, unlike Trezor).</summary>
public static class ColdcardUsb
{
	public const ushort VendorId = 0xd13e;
	public const ushort ProductId = 0xcc10;

	/// <summary>Opens the connected Coldcard, optionally pinned to a serial number. Throws if none is found.</summary>
	public static IUsbHid Open(string? serialNumber = null) =>
		UsbHid.TryOpen(VendorId, ProductId, serialNumber)
		?? throw new InvalidOperationException(serialNumber is null
			? "No Coldcard is connected. Connect (and Enable) USB on the device."
			: $"Coldcard with serial '{serialNumber}' not found.");

	/// <summary>Serial numbers of the connected Coldcards (empty when none are attached).</summary>
	public static IReadOnlyList<string> Enumerate() => UsbHid.Enumerate(VendorId, ProductId);
}
