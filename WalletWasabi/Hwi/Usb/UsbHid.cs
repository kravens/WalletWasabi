using System.Runtime.InteropServices;

namespace WalletWasabi.Hwi.Usb;

/// <summary>
/// Minimal HID access to a device that speaks a raw report protocol over USB, without any external
/// dependency or bridge daemon: <see cref="UsbHidWindows"/> (hid.dll + SetupAPI), <see cref="UsbHidLinux"/>
/// (hidraw) and <see cref="UsbHidMacOs"/> (IOKit). Every such device we drive uses 64-byte reports.
/// </summary>
public interface IUsbHid : IDisposable
{
	/// <summary>Writes one 65-byte output report (report id byte + 64 bytes of frame).</summary>
	void WriteReport(byte[] report65);

	/// <summary>Reads one 64-byte input report, or null on timeout.</summary>
	byte[]? ReadReport(int timeoutMs);
}

public static class UsbHid
{
	/// <summary>Output reports are 65 bytes: a leading report-id byte (0) plus the 64-byte frame.</summary>
	public const int OutputReportLength = 65;

	/// <summary>Input reports are 64 bytes (no report-id byte on the read side).</summary>
	public const int InputReportLength = 64;

	/// <summary>Opens the connected device with this USB identity, optionally pinned to a serial number; null when none is attached.</summary>
	/// <param name="usagePage">The HID usage page of the wanted interface, for a device that exposes several (a Passport Prime also has a FIDO interface); null takes any.</param>
	public static IUsbHid? TryOpen(ushort vendorId, ushort productId, string? serialNumber = null, ushort? usagePage = null)
	{
		if (OperatingSystem.IsWindows())
		{
			return UsbHidWindows.Open(vendorId, productId, serialNumber, usagePage);
		}

		if (OperatingSystem.IsLinux())
		{
			return UsbHidLinux.Open(vendorId, productId, serialNumber, usagePage);
		}

		if (OperatingSystem.IsMacOS())
		{
			return UsbHidMacOs.Open(vendorId, productId, serialNumber, usagePage);
		}

		throw new PlatformNotSupportedException($"The USB HID transport has no implementation for {RuntimeInformation.OSDescription}.");
	}

	/// <summary>Serial numbers of the connected devices with this USB identity (empty when none are attached).</summary>
	public static IReadOnlyList<string> Enumerate(ushort vendorId, ushort productId, ushort? usagePage = null)
	{
		if (OperatingSystem.IsWindows())
		{
			return UsbHidWindows.Enumerate(vendorId, productId, usagePage);
		}

		if (OperatingSystem.IsLinux())
		{
			return UsbHidLinux.Enumerate(vendorId, productId, usagePage);
		}

		if (OperatingSystem.IsMacOS())
		{
			return UsbHidMacOs.Enumerate(vendorId, productId, usagePage);
		}

		return [];
	}
}
