using WalletWasabi.Hwi.Usb;

namespace WalletWasabi.Hwi.Passport;

/// <summary>The USB identity of a Foundation Passport Prime running the coinjoin <c>wallet-rpc</c> service,
/// a vendor HID interface with 64-byte reports driven the same way a Coldcard is.</summary>
public static class PassportUsb
{
	// Placeholders until read off real hardware: the wallet-rpc interface has no host transport in the
	// KeyOS SDK yet, so no device has advertised them.
	public const ushort VendorId = 0x1209;
	public const ushort ProductId = 0x7853;

	/// <summary>Opens the connected Passport, optionally pinned to a serial number. Throws if none is found.</summary>
	public static IUsbHid Open(string? serialNumber = null) =>
		UsbHid.TryOpen(VendorId, ProductId, serialNumber)
		?? throw new PassportException(serialNumber is null
			? "No Passport Prime is connected (or its wallet-rpc service is not running)."
			: $"Passport Prime with serial '{serialNumber}' not found.");

	/// <summary>Serial numbers of the connected Passports (empty when none are attached).</summary>
	public static IReadOnlyList<string> Enumerate() => UsbHid.Enumerate(VendorId, ProductId);
}
