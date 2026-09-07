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
	public static IUsbHid? TryOpen(ushort vendorId, ushort productId, string? serialNumber = null)
	{
		if (OperatingSystem.IsWindows())
		{
			return UsbHidWindows.Open(vendorId, productId, serialNumber);
		}

		if (OperatingSystem.IsLinux())
		{
			return UsbHidLinux.Open(vendorId, productId, serialNumber);
		}

		if (OperatingSystem.IsMacOS())
		{
			return UsbHidMacOs.Open(vendorId, productId, serialNumber);
		}

		throw new PlatformNotSupportedException($"The USB HID transport has no implementation for {RuntimeInformation.OSDescription}.");
	}

	/// <summary>Serial numbers of the connected devices with this USB identity (empty when none are attached).</summary>
	public static IReadOnlyList<string> Enumerate(ushort vendorId, ushort productId)
	{
		if (OperatingSystem.IsWindows())
		{
			return UsbHidWindows.Enumerate(vendorId, productId);
		}

		if (OperatingSystem.IsLinux())
		{
			return UsbHidLinux.Enumerate(vendorId, productId);
		}

		if (OperatingSystem.IsMacOS())
		{
			return UsbHidMacOs.Enumerate(vendorId, productId);
		}

		return [];
	}
}
