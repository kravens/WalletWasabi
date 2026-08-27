using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace WalletWasabi.Hwi.Passport;

/// <summary>
/// Windows HID access to a Passport Prime via the built-in <c>hid.dll</c> + <c>setupapi.dll</c> (no external
/// dependency), matching the Coldcard transport approach. Enumerates the HID interface class, filters to the
/// Passport wallet-rpc VID:PID, opens with <c>CreateFile</c>, and does fixed-size <c>ReadFile</c>/<c>WriteFile</c>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class PassportHidWindows : IPassportHid
{
	private readonly SafeFileHandle _handle;

	private PassportHidWindows(SafeFileHandle handle)
	{
		_handle = handle;
	}

	public static IReadOnlyList<string> Enumerate()
	{
		var serials = new List<string>();
		foreach (var path in EnumeratePassportPaths())
		{
			if (TryReadSerial(path) is { } serial)
			{
				serials.Add(serial);
			}
		}
		return serials;
	}

	public static PassportHidWindows Open(string? serialNumber)
	{
		foreach (var path in EnumeratePassportPaths())
		{
			if (serialNumber is not null && TryReadSerial(path) != serialNumber)
			{
				continue;
			}

			var handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
				IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
			if (!handle.IsInvalid)
			{
				return new PassportHidWindows(handle);
			}
			handle.Dispose();
		}

		throw new InvalidOperationException(serialNumber is null
			? "No Passport Prime found. Connect and unlock the device, and enable the wallet-rpc interface."
			: $"Passport Prime with serial '{serialNumber}' not found.");
	}

	public void WriteReport(byte[] report65)
	{
		if (report65.Length != PassportUsb.OutputReportLength)
		{
			throw new ArgumentException($"Output report must be {PassportUsb.OutputReportLength} bytes.", nameof(report65));
		}

		if (!WriteFile(_handle, report65, (uint)report65.Length, out uint written, IntPtr.Zero) || written != report65.Length)
		{
			throw new IOException($"Passport HID write failed (wrote {written} of {report65.Length}).");
		}
	}

	public byte[]? ReadReport(int timeoutMs)
	{
		// Opened without FILE_FLAG_OVERLAPPED, so ReadFile blocks. Bound it with a wait on the handle so a
		// stalled device does not hang the caller.
		if (WaitForSingleObject(_handle, (uint)Math.Max(0, timeoutMs)) != WAIT_OBJECT_0)
		{
			return null;
		}

		var buffer = new byte[PassportUsb.InputReportLength];
		if (!ReadFile(_handle, buffer, (uint)buffer.Length, out uint read, IntPtr.Zero))
		{
			throw new IOException("Passport HID read failed.");
		}
		if (read == 0)
		{
			return null;
		}

		return read == buffer.Length ? buffer : buffer[..(int)read];
	}

	public void Dispose() => _handle.Dispose();

	private static IEnumerable<string> EnumeratePassportPaths()
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
				if (path is not null && MatchesPassport(path))
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

	private static bool MatchesPassport(string devicePath)
	{
		using var handle = CreateFile(devicePath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
		if (handle.IsInvalid)
		{
			return false;
		}

		var attributes = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
		return HidD_GetAttributes(handle, ref attributes)
			&& attributes.VendorID == PassportUsb.VendorId
			&& attributes.ProductID == PassportUsb.ProductId;
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
			Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);
			if (!SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, detailBuffer, requiredSize, out _, IntPtr.Zero))
			{
				return null;
			}

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
