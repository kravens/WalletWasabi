using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace WalletWasabi.Hwi.Coldcard;

/// <summary>
/// Windows HID access to a Coldcard via the built-in <c>hid.dll</c> + <c>setupapi.dll</c> (no external
/// dependency). Enumerates the HID interface class, filters to the Coldcard VID:PID, opens the device with
/// <c>CreateFile</c>, and does overlapped-free <c>ReadFile</c>/<c>WriteFile</c> of fixed-size reports.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ColdcardHidWindows : IColdcardHid
{
	private readonly SafeFileHandle _handle;

	private ColdcardHidWindows(SafeFileHandle handle)
	{
		_handle = handle;
	}

	public static IReadOnlyList<string> Enumerate()
	{
		var serials = new List<string>();
		foreach (var (path, _) in EnumerateColdcardPaths())
		{
			if (TryReadSerial(path) is { } serial)
			{
				serials.Add(serial);
			}
		}
		return serials;
	}

	public static ColdcardHidWindows Open(string? serialNumber)
	{
		foreach (var (path, _) in EnumerateColdcardPaths())
		{
			if (serialNumber is not null && TryReadSerial(path) != serialNumber)
			{
				continue;
			}

			var handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
				IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
			if (!handle.IsInvalid)
			{
				return new ColdcardHidWindows(handle);
			}
			handle.Dispose();
		}

		throw new InvalidOperationException(serialNumber is null
			? "No Coldcard found. Connect and unlock the device."
			: $"Coldcard with serial '{serialNumber}' not found.");
	}

	public void WriteReport(byte[] report65)
	{
		if (report65.Length != ColdcardUsb.OutputReportLength)
		{
			throw new ArgumentException($"Output report must be {ColdcardUsb.OutputReportLength} bytes.", nameof(report65));
		}

		if (!WriteFile(_handle, report65, (uint)report65.Length, out uint written, IntPtr.Zero) || written != report65.Length)
		{
			throw new IOException($"Coldcard HID write failed (wrote {written} of {report65.Length}).");
		}
	}

	public byte[]? ReadReport(int timeoutMs)
	{
		// The HID handle is opened without FILE_FLAG_OVERLAPPED, so ReadFile blocks. Bound it with a wait
		// on the handle so a stalled device does not hang the caller.
		if (WaitForSingleObject(_handle, (uint)Math.Max(0, timeoutMs)) != WAIT_OBJECT_0)
		{
			return null;
		}

		// Windows requires the read buffer to be the HID InputReportByteLength, which always includes a
		// leading report-id byte (0 for the Coldcard) before the 64 data bytes; a 64-byte buffer makes
		// ReadFile fail with ERROR_INVALID_USER_BUFFER.
		var buffer = new byte[ColdcardUsb.InputReportLength + 1];
		if (!ReadFile(_handle, buffer, (uint)buffer.Length, out uint read, IntPtr.Zero))
		{
			throw new IOException($"Coldcard HID read failed (win32 error {Marshal.GetLastWin32Error()}).");
		}
		if (read <= 1)
		{
			return null;
		}

		// Strip the report-id byte; the caller sees the 64 data bytes the device sent.
		return buffer[1..(int)read];
	}

	public void Dispose() => _handle.Dispose();

	private static IEnumerable<(string Path, Guid Interface)> EnumerateColdcardPaths()
	{
		HidD_GetHidGuid(out Guid hidGuid);
		var deviceInfoSet = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
		if (deviceInfoSet == INVALID_HANDLE_VALUE)
		{
			yield break;
		}

		try
		{
			var interfaceData = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
			for (uint index = 0; SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref hidGuid, index, ref interfaceData); index++)
			{
				string? path = GetDevicePath(deviceInfoSet, ref interfaceData);
				if (path is null)
				{
					continue;
				}

				if (MatchesColdcard(path))
				{
					yield return (path, hidGuid);
				}
			}
		}
		finally
		{
			SetupDiDestroyDeviceInfoList(deviceInfoSet);
		}
	}

	private static bool MatchesColdcard(string devicePath)
	{
		using var handle = CreateFile(devicePath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
		if (handle.IsInvalid)
		{
			return false;
		}

		var attributes = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
		return HidD_GetAttributes(handle, ref attributes)
			&& attributes.VendorID == ColdcardUsb.VendorId
			&& attributes.ProductID == ColdcardUsb.ProductId;
	}

	private static string? TryReadSerial(string devicePath)
	{
		using var handle = CreateFile(devicePath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
		if (handle.IsInvalid)
		{
			return null;
		}

		var buffer = new char[128];
		return HidD_GetSerialNumberString(handle, buffer, (uint)(buffer.Length * sizeof(char)))
			? new string(buffer).TrimEnd('\0')
			: null;
	}

	private static string? GetDevicePath(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA interfaceData)
	{
		SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, out uint requiredSize, IntPtr.Zero);
		if (requiredSize == 0)
		{
			return null;
		}

		var detailBuffer = Marshal.AllocHGlobal((int)requiredSize);
		try
		{
			// cbSize is the size of the fixed part of SP_DEVICE_INTERFACE_DETAIL_DATA (4 on 32-bit + padding),
			// which is 8 on 64-bit due to alignment of the char[] that follows.
			Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);
			if (!SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, detailBuffer, requiredSize, out _, IntPtr.Zero))
			{
				return null;
			}

			// The device path (wide string) follows the cbSize field.
			return Marshal.PtrToStringUni(detailBuffer + 4);
		}
		finally
		{
			Marshal.FreeHGlobal(detailBuffer);
		}
	}

	// --- P/Invoke ---

	private const uint GENERIC_READ = 0x80000000;
	private const uint GENERIC_WRITE = 0x40000000;
	private const uint FILE_SHARE_READ = 0x1;
	private const uint FILE_SHARE_WRITE = 0x2;
	private const uint OPEN_EXISTING = 3;
	private const uint DIGCF_PRESENT = 0x2;
	private const uint DIGCF_DEVICEINTERFACE = 0x10;
	private const uint WAIT_OBJECT_0 = 0x0;
	private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

	[StructLayout(LayoutKind.Sequential)]
	private struct SP_DEVICE_INTERFACE_DATA
	{
		public uint cbSize;
		public Guid InterfaceClassGuid;
		public uint Flags;
		public IntPtr Reserved;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct HIDD_ATTRIBUTES
	{
		public uint Size;
		public ushort VendorID;
		public ushort ProductID;
		public ushort VersionNumber;
	}

	[DllImport("hid.dll")]
	private static extern void HidD_GetHidGuid(out Guid hidGuid);

	[DllImport("hid.dll")]
	private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HIDD_ATTRIBUTES attributes);

	[DllImport("hid.dll", CharSet = CharSet.Unicode)]
	private static extern bool HidD_GetSerialNumberString(SafeFileHandle handle, char[] buffer, uint bufferLength);

	[DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

	[DllImport("setupapi.dll")]
	private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA interfaceData);

	[DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
	private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA interfaceData, IntPtr detailData, uint detailSize, out uint requiredSize, IntPtr deviceInfoData);

	[DllImport("setupapi.dll")]
	private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool ReadFile(SafeFileHandle handle, byte[] buffer, uint bytesToRead, out uint bytesRead, IntPtr overlapped);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool WriteFile(SafeFileHandle handle, byte[] buffer, uint bytesToWrite, out uint bytesWritten, IntPtr overlapped);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
}
