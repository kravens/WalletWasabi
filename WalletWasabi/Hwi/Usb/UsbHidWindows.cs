using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace WalletWasabi.Hwi.Usb;

/// <summary>
/// Windows HID access via the built-in <c>hid.dll</c> + <c>setupapi.dll</c> (no external dependency).
/// Enumerates the HID interface class, filters to the wanted VID:PID, opens the device with
/// <c>CreateFile</c>, and does overlapped-free <c>ReadFile</c>/<c>WriteFile</c> of fixed-size reports.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class UsbHidWindows : IUsbHid
{
	private readonly SafeFileHandle _handle;

	private UsbHidWindows(SafeFileHandle handle)
	{
		_handle = handle;
	}

	public static IReadOnlyList<string> Enumerate(ushort vendorId, ushort productId, ushort? usagePage)
	{
		var serials = new List<string>();
		foreach (var path in EnumeratePaths(vendorId, productId, usagePage))
		{
			if (TryReadSerial(path) is { } serial)
			{
				serials.Add(serial);
			}
		}
		return serials;
	}

	public static UsbHidWindows? Open(ushort vendorId, ushort productId, string? serialNumber, ushort? usagePage)
	{
		foreach (var path in EnumeratePaths(vendorId, productId, usagePage))
		{
			if (serialNumber is not null && TryReadSerial(path) != serialNumber)
			{
				continue;
			}

			var handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
				IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
			if (!handle.IsInvalid)
			{
				return new UsbHidWindows(handle);
			}
			handle.Dispose();
		}

		return null;
	}

	public void WriteReport(byte[] report65)
	{
		if (report65.Length != UsbHid.OutputReportLength)
		{
			throw new ArgumentException($"Output report must be {UsbHid.OutputReportLength} bytes.", nameof(report65));
		}

		if (!WriteFile(_handle, report65, (uint)report65.Length, out uint written, IntPtr.Zero) || written != report65.Length)
		{
			throw new IOException($"HID write failed (wrote {written} of {report65.Length}).");
		}
	}

	public byte[]? ReadReport(int timeoutMs)
	{
		// The handle is synchronous, so ReadFile blocks until a report arrives. Waiting on the handle
		// itself is useless (a file handle with no I/O in flight is always signaled), so the timeout is
		// enforced by cancelling the blocked read from this thread with CancelIoEx.
		//
		// Windows requires the read buffer to be the HID InputReportByteLength, which always includes a
		// leading report-id byte (0 for these devices) before the 64 data bytes; a 64-byte buffer makes
		// ReadFile fail with ERROR_INVALID_USER_BUFFER.
		var buffer = new byte[UsbHid.InputReportLength + 1];
		var read = Task.Run(() =>
		{
			bool ok = ReadFile(_handle, buffer, (uint)buffer.Length, out uint count, IntPtr.Zero);
			return (Ok: ok, Count: count, Error: ok ? 0 : Marshal.GetLastWin32Error());
		});

		if (!read.Wait(Math.Max(0, timeoutMs)))
		{
			CancelIoEx(_handle, IntPtr.Zero);
			read.Wait(1000); // reap the cancelled read
			return null;
		}

		var (success, readCount, error) = read.Result;
		if (!success)
		{
			if (error == ERROR_OPERATION_ABORTED)
			{
				return null;
			}
			throw new IOException($"HID read failed (win32 error {error}).");
		}
		if (readCount <= 1)
		{
			return null;
		}

		// Strip the report-id byte; the caller sees the 64 data bytes the device sent.
		return buffer[1..(int)readCount];
	}

	public void Dispose() => _handle.Dispose();

	private static IEnumerable<string> EnumeratePaths(ushort vendorId, ushort productId, ushort? usagePage)
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
				if (path is not null && Matches(path, vendorId, productId, usagePage))
				{
					yield return path;
				}
			}
		}
		finally
		{
			SetupDiDestroyDeviceInfoList(deviceInfoSet);
		}
	}

	private static bool Matches(string devicePath, ushort vendorId, ushort productId, ushort? usagePage)
	{
		using var handle = CreateFile(devicePath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
		if (handle.IsInvalid)
		{
			return false;
		}

		var attributes = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
		return HidD_GetAttributes(handle, ref attributes)
			&& attributes.VendorID == vendorId
			&& attributes.ProductID == productId
			&& (usagePage is null || UsagePageOf(handle) == usagePage);
	}

	/// <summary>The top-level usage page of the interface's report descriptor, or null when Windows will not say.</summary>
	private static ushort? UsagePageOf(SafeFileHandle handle)
	{
		if (!HidD_GetPreparsedData(handle, out var preparsed))
		{
			return null;
		}

		try
		{
			var caps = default(HIDP_CAPS);
			return HidP_GetCaps(preparsed, ref caps) == HIDP_STATUS_SUCCESS ? caps.UsagePage : null;
		}
		finally
		{
			HidD_FreePreparsedData(preparsed);
		}
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
	private const int ERROR_OPERATION_ABORTED = 995;
	private const int HIDP_STATUS_SUCCESS = 0x00110000;
	private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

	[StructLayout(LayoutKind.Sequential)]
	private struct SP_DEVICE_INTERFACE_DATA
	{
		public uint cbSize;
		public Guid InterfaceClassGuid;
		public uint Flags;
		public IntPtr Reserved;
	}

	/// <summary>Only the first two fields are read; the size keeps the 64 bytes Windows checks.</summary>
	[StructLayout(LayoutKind.Sequential, Size = 64)]
	private struct HIDP_CAPS
	{
		public ushort Usage;
		public ushort UsagePage;
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

	[DllImport("hid.dll")]
	private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr preparsedData);

	[DllImport("hid.dll")]
	private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

	[DllImport("hid.dll")]
	private static extern int HidP_GetCaps(IntPtr preparsedData, ref HIDP_CAPS capabilities);

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
	private static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);
}
