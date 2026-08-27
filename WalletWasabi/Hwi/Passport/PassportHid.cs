using System.Collections.Generic;

namespace WalletWasabi.Hwi.Passport;

/// <summary>
/// Minimal HID access to a Foundation Passport Prime running the coinjoin <c>wallet-rpc</c> firmware
/// service. Prime exposes a vendor HID interface (usage page 0xFF00, 64-byte interrupt endpoints); the
/// transport is hand-rolled per platform the same way <see cref="Coldcard.IColdcardHid"/> is, with no
/// external dependency and no bridge daemon.
/// </summary>
public interface IPassportHid : IDisposable
{
	/// <summary>Writes one 65-byte output report (report id byte + 64-byte frame).</summary>
	void WriteReport(byte[] report65);

	/// <summary>Reads one 64-byte input report, or null on timeout.</summary>
	byte[]? ReadReport(int timeoutMs);
}

/// <summary>The USB identity of a Passport Prime and the report geometry of its wallet-rpc HID protocol.</summary>
public static class PassportUsb
{
	// Passport Prime's wallet-rpc interface. The device advertises Foundation's assigned VID; the PID is
	// the wallet-rpc HID interface. Confirm against a physical device with `HidD_GetAttributes` (see
	// PASSPORT_TESTING.md) — these are placeholders until read off real hardware.
	public const ushort VendorId = 0x1209; // pid.codes / Foundation-assigned; verify on device
	public const ushort ProductId = 0x7853; // wallet-rpc interface; verify on device

	/// <summary>Output reports are 65 bytes: a leading report-id byte (0) plus the 64-byte frame.</summary>
	public const int OutputReportLength = 65;

	/// <summary>Input reports are 64 bytes (no report-id byte on the read side).</summary>
	public const int InputReportLength = 64;

	/// <summary>Opens the connected Passport (optionally pinned to a serial). Throws if none is found.</summary>
	public static IPassportHid Open(string? serialNumber = null)
	{
		if (OperatingSystem.IsWindows())
		{
			return PassportHidWindows.Open(serialNumber);
		}

		throw new PlatformNotSupportedException("Passport HID transport is currently implemented for Windows only.");
	}

	/// <summary>Serial numbers of the connected Passports (empty when none are attached).</summary>
	public static IReadOnlyList<string> Enumerate()
	{
		if (OperatingSystem.IsWindows())
		{
			return PassportHidWindows.Enumerate();
		}

		return [];
	}
}
