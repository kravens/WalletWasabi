using System.Collections.Generic;

namespace WalletWasabi.Hwi.Coldcard;

/// <summary>
/// Minimal HID access to a Coldcard, without any external dependency. Coldcard speaks a raw HID
/// protocol (no bridge daemon, unlike Trezor), so the transport is hand-rolled per platform:
/// <see cref="ColdcardHidWindows"/> (hid.dll + SetupAPI), Linux hidraw and macOS IOKit follow.
/// </summary>
public interface IColdcardHid : IDisposable
{
	/// <summary>Writes one 65-byte output report (report id byte + 64 bytes of frame).</summary>
	void WriteReport(byte[] report65);

	/// <summary>Reads one 64-byte input report, or null on timeout.</summary>
	byte[]? ReadReport(int timeoutMs);
}

/// <summary>The USB identity of a Coldcard and the report geometry of its HID protocol.</summary>
public static class ColdcardUsb
{
	public const ushort VendorId = 0xd13e;
	public const ushort ProductId = 0xcc10;

	/// <summary>Output reports are 65 bytes: a leading report-id byte (0) plus the 64-byte frame.</summary>
	public const int OutputReportLength = 65;

	/// <summary>Input reports are 64 bytes (no report-id byte on the read side).</summary>
	public const int InputReportLength = 64;

	/// <summary>Opens the connected Coldcard, optionally pinned to a serial number. Throws if none is found.</summary>
	public static IColdcardHid Open(string? serialNumber = null)
	{
		if (OperatingSystem.IsWindows())
		{
			return ColdcardHidWindows.Open(serialNumber);
		}

		if (OperatingSystem.IsLinux())
		{
			return ColdcardHidLinux.Open(serialNumber);
		}

		throw new PlatformNotSupportedException("The Coldcard HID transport is implemented for Windows and Linux; macOS (IOKit) is not written yet.");
	}

	/// <summary>Serial numbers of the connected Coldcards (empty when none are attached).</summary>
	public static IReadOnlyList<string> Enumerate()
	{
		if (OperatingSystem.IsWindows())
		{
			return ColdcardHidWindows.Enumerate();
		}

		if (OperatingSystem.IsLinux())
		{
			return ColdcardHidLinux.Enumerate();
		}

		return [];
	}
}
