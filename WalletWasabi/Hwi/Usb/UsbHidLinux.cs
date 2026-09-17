using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WalletWasabi.Hwi.Usb;

/// <summary>
/// Linux HID access through <c>hidraw</c>, with no external dependency: the devices are found by reading
/// <c>/sys/class/hidraw/*/device/uevent</c> and driven with plain read/write on the character device.
/// <para>
/// One asymmetry to be careful about, and it differs from Windows. A write must still carry the leading
/// report-id byte, so the 65-byte frame goes out as-is. A read does not: for a device with no report IDs
/// the kernel hands back the 64 data bytes alone, with nothing to strip. Treating a read like the Windows
/// path (65 bytes, discard the first) would silently shift every reply by one byte.
/// </para>
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class UsbHidLinux : IUsbHid
{
	private const string HidrawClass = "/sys/class/hidraw";
	private const int O_RDWR = 2;
	private const short POLLIN = 0x001;

	private readonly int _fd;
	private bool _disposed;

	private UsbHidLinux(int fd)
	{
		_fd = fd;
	}

	public static IReadOnlyList<string> Enumerate(ushort vendorId, ushort productId, ushort? usagePage) =>
		EnumerateDevices(vendorId, productId, usagePage).Select(x => x.Serial).Where(x => x is not null).Cast<string>().ToList();

	public static UsbHidLinux? Open(ushort vendorId, ushort productId, string? serialNumber, ushort? usagePage)
	{
		foreach (var (node, serial) in EnumerateDevices(vendorId, productId, usagePage))
		{
			if (serialNumber is not null && serial != serialNumber)
			{
				continue;
			}

			int fd = open(node, O_RDWR);
			if (fd >= 0)
			{
				return new UsbHidLinux(fd);
			}

			// Almost always a permissions problem rather than a missing device: hidraw nodes are
			// root-only until a udev rule grants the user access, and that is worth saying outright.
			int err = Marshal.GetLastWin32Error();
			throw new IOException(
				$"Cannot open '{node}' (errno {err}). If this is a permissions error, add a udev rule for "
				+ $"{vendorId:x4}:{productId:x4} or run with the rights to read it.");
		}

		return null;
	}

	/// <summary>Every hidraw node whose uevent reports the wanted vendor and product (and, when asked, whose
	/// report descriptor opens with the wanted usage page), with the serial the uevent carries as HID_UNIQ.</summary>
	/// <param name="classRoot">Where to look. Only tests pass anything else: the parsing is the part that
	/// can quietly be wrong (hex widths, a missing HID_UNIQ, an unrelated device sitting alongside), and it
	/// cannot be covered otherwise without a device plugged into a Linux box.</param>
	internal static IEnumerable<(string Node, string? Serial)> EnumerateDevices(ushort vendorId, ushort productId, ushort? usagePage = null, string classRoot = HidrawClass)
	{
		if (!Directory.Exists(classRoot))
		{
			yield break;
		}

		foreach (var entry in Directory.GetDirectories(classRoot).Order())
		{
			var name = Path.GetFileName(entry);
			string? serial = null;
			bool match = false;

			// HID_ID=0003:0000D13E:0000CC10 — bus, vendor, product, each zero-padded hex.
			foreach (var line in ReadLinesOrEmpty(Path.Combine(entry, "device", "uevent")))
			{
				if (line.StartsWith("HID_ID=", StringComparison.Ordinal))
				{
					var parts = line[7..].Split(':');
					match = parts.Length == 3
						&& int.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out var vid)
						&& int.TryParse(parts[2], System.Globalization.NumberStyles.HexNumber, null, out var pid)
						&& vid == vendorId
						&& pid == productId;
				}
				else if (line.StartsWith("HID_UNIQ=", StringComparison.Ordinal))
				{
					serial = line[9..].Trim();
				}
			}

			if (match && (usagePage is null || UsagePageOf(Path.Combine(entry, "device", "report_descriptor")) == usagePage))
			{
				yield return ($"/dev/{name}", string.IsNullOrEmpty(serial) ? null : serial);
			}
		}
	}

	/// <summary>The Usage Page item a report descriptor opens with: <c>05 pp</c> or <c>06 lo hi</c>; null when unreadable.</summary>
	private static ushort? UsagePageOf(string reportDescriptorPath)
	{
		try
		{
			return File.ReadAllBytes(reportDescriptorPath) switch
			{
				[0x05, var page, ..] => page,
				[0x06, var low, var high, ..] => (ushort)(low | (high << 8)),
				_ => null,
			};
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	private static string[] ReadLinesOrEmpty(string path)
	{
		try
		{
			return File.ReadAllLines(path);
		}
		catch (IOException)
		{
			return [];
		}
		catch (UnauthorizedAccessException)
		{
			return [];
		}
	}

	public void WriteReport(byte[] report65)
	{
		if (report65.Length != UsbHid.OutputReportLength)
		{
			throw new ArgumentException($"Output report must be {UsbHid.OutputReportLength} bytes.", nameof(report65));
		}

		// The leading report-id byte stays: hidraw expects it even when the device has no report IDs.
		nint written = write(_fd, report65, report65.Length);
		if (written != report65.Length)
		{
			throw new IOException($"HID write failed (wrote {written} of {report65.Length}, errno {Marshal.GetLastWin32Error()}).");
		}
	}

	public byte[]? ReadReport(int timeoutMs)
	{
		var fds = new PollFd { fd = _fd, events = POLLIN, revents = 0 };
		int ready = poll(ref fds, 1, Math.Max(0, timeoutMs));
		if (ready == 0)
		{
			return null; // timed out; the caller decides whether that ends the exchange
		}
		if (ready < 0)
		{
			throw new IOException($"HID poll failed (errno {Marshal.GetLastWin32Error()}).");
		}

		// No report-id byte on the way in, so this is the frame itself.
		var buffer = new byte[UsbHid.InputReportLength];
		nint count = read(_fd, buffer, buffer.Length);
		if (count < 0)
		{
			throw new IOException($"HID read failed (errno {Marshal.GetLastWin32Error()}).");
		}
		if (count == 0)
		{
			return null;
		}

		return count == buffer.Length ? buffer : buffer[..(int)count];
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			_disposed = true;
			close(_fd);
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct PollFd
	{
		public int fd;
		public short events;
		public short revents;
	}

	[DllImport("libc", SetLastError = true, EntryPoint = "open")]
	private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

	[DllImport("libc", SetLastError = true, EntryPoint = "read")]
	private static extern nint read(int fd, byte[] buf, nint count);

	[DllImport("libc", SetLastError = true, EntryPoint = "write")]
	private static extern nint write(int fd, byte[] buf, nint count);

	[DllImport("libc", SetLastError = true, EntryPoint = "close")]
	private static extern int close(int fd);

	[DllImport("libc", SetLastError = true, EntryPoint = "poll")]
	private static extern int poll(ref PollFd fds, uint nfds, int timeout);
}
