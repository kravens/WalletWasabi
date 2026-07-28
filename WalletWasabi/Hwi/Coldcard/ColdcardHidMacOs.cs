using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace WalletWasabi.Hwi.Coldcard;

/// <summary>
/// macOS HID access to a Coldcard through IOKit, with no external dependency.
/// <para>
/// Two asymmetries against the other platforms, both of which corrupt the stream silently if missed.
/// </para>
/// <para>
/// <b>Writing.</b> Windows and Linux take the 65-byte frame as-is, leading report-id byte included.
/// IOKit takes the report id as its own argument, so the id is stripped and 64 bytes are sent with
/// <c>reportID = 0</c>. Sending all 65 would push a spurious zero into the frame and every reply
/// afterwards would be one byte out.
/// </para>
/// <para>
/// <b>Reading.</b> There is no blocking read. Input reports are delivered to a callback that only fires
/// while a run loop is running, and run loops belong to the thread that runs them. Since the device is
/// driven from whatever thread the coinjoin flow happens to be on, the run loop gets a thread of its own
/// and hands frames over through a queue; <see cref="ReadReport"/> then waits on the queue. Scheduling on
/// the caller's thread instead would work exactly until the first call arrived on a different one.
/// </para>
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class ColdcardHidMacOs : IColdcardHid
{
	private const int KIOHIDReportTypeOutput = 1;
	private const int KIOReturnSuccess = 0;

	private readonly nint _device;
	private readonly BlockingCollection<byte[]> _received = new();
	private readonly Thread _runLoopThread;
	private readonly ManualResetEventSlim _runLoopReady = new(false);

	// Both must outlive the registration: the callback is invoked from unmanaged code, and the buffer is
	// written by it. Letting either be collected or moved is a use-after-free that shows up as garbage
	// frames long after the fact.
	private readonly IOHIDReportCallback _callback;
	private readonly byte[] _inputBuffer = new byte[ColdcardUsb.InputReportLength];
	private GCHandle _inputBufferHandle;

	private nint _runLoop;
	private bool _disposed;

	private ColdcardHidMacOs(nint device)
	{
		_device = device;
		_callback = OnInputReport;
		_inputBufferHandle = GCHandle.Alloc(_inputBuffer, GCHandleType.Pinned);

		_runLoopThread = new Thread(RunLoop) { IsBackground = true, Name = "Coldcard HID (IOKit)" };
		_runLoopThread.Start();

		// Nothing can be read until the callback is scheduled, so wait for the loop to be up rather than
		// racing the first exchange.
		if (!_runLoopReady.Wait(TimeSpan.FromSeconds(5)))
		{
			throw new IOException("The Coldcard HID run loop did not start.");
		}
	}

	public static IReadOnlyList<string> Enumerate() =>
		EnumerateColdcards().Select(x => x.Serial).Where(x => x is not null).Cast<string>().ToList();

	public static ColdcardHidMacOs Open(string? serialNumber)
	{
		foreach (var (device, serial) in EnumerateColdcards())
		{
			if (serialNumber is not null && serial != serialNumber)
			{
				continue;
			}

			// kIOHIDOptionsTypeSeizeDevice is deliberately not used: seizing would lock out anything else
			// talking to the card, and the point here is to coexist rather than to take it over.
			var result = IOHIDDeviceOpen(device, 0);
			if (result != KIOReturnSuccess)
			{
				throw new IOException(
					$"Cannot open the Coldcard (IOKit error 0x{result:x8}). Another application may already "
					+ "have it open.");
			}

			return new ColdcardHidMacOs(device);
		}

		throw new InvalidOperationException(serialNumber is null
			// A switched-off USB port looks exactly like an unplugged device from here, and on a Mk4 it is
			// a setting rather than a fault, so name both rather than sending the user to check the cable.
			? "Connect (and Enable) USB"
			: $"Coldcard with serial '{serialNumber}' not found.");
	}

	/// <summary>Every attached device matching the Coldcard's vendor and product, with its serial.</summary>
	private static IEnumerable<(nint Device, string? Serial)> EnumerateColdcards()
	{
		var manager = IOHIDManagerCreate(nint.Zero, 0);
		if (manager == nint.Zero)
		{
			yield break;
		}

		var matching = CreateMatchingDictionary();
		IOHIDManagerSetDeviceMatching(manager, matching);
		CFRelease(matching);

		var devices = IOHIDManagerCopyDevices(manager);
		if (devices == nint.Zero)
		{
			CFRelease(manager);
			yield break;
		}

		var count = (int)CFSetGetCount(devices);
		var buffer = new nint[count];
		CFSetGetValues(devices, buffer);

		foreach (var device in buffer)
		{
			if (device != nint.Zero)
			{
				yield return (device, GetStringProperty(device, "SerialNumber"));
			}
		}

		// The set is released but the devices are not: IOHIDManagerCopyDevices does not hand over a
		// reference to each device, and the returned handle has to stay valid for the caller to open.
		CFRelease(devices);
		CFRelease(manager);
	}

	private static nint CreateMatchingDictionary()
	{
		var dict = CFDictionaryCreateMutable(nint.Zero, 0, nint.Zero, nint.Zero);
		SetNumber(dict, "VendorID", ColdcardUsb.VendorId);
		SetNumber(dict, "ProductID", ColdcardUsb.ProductId);
		return dict;

		static void SetNumber(nint dict, string key, int value)
		{
			var cfKey = CreateCfString(key);
			var cfValue = CFNumberCreate(nint.Zero, CFNumberType.SInt32, ref value);
			CFDictionarySetValue(dict, cfKey, cfValue);
			CFRelease(cfKey);
			CFRelease(cfValue);
		}
	}

	private static string? GetStringProperty(nint device, string key)
	{
		var cfKey = CreateCfString(key);
		try
		{
			var value = IOHIDDeviceGetProperty(device, cfKey);
			if (value == nint.Zero)
			{
				return null;
			}

			// 256 is generous for a serial; CFStringGetCString fails rather than truncating if it is not.
			var buffer = new byte[256];
			return CFStringGetCString(value, buffer, buffer.Length, CFStringEncoding.Utf8)
				? System.Text.Encoding.UTF8.GetString(buffer).TrimEnd('\0')
				: null;
		}
		finally
		{
			CFRelease(cfKey);
		}
	}

	private void RunLoop()
	{
		_runLoop = CFRunLoopGetCurrent();

		IOHIDDeviceRegisterInputReportCallback(
			_device,
			_inputBufferHandle.AddrOfPinnedObject(),
			ColdcardUsb.InputReportLength,
			_callback,
			nint.Zero);

		var mode = CreateCfString("kCFRunLoopDefaultMode");
		IOHIDDeviceScheduleWithRunLoop(_device, _runLoop, mode);

		_runLoopReady.Set();

		// Returns when CFRunLoopStop is called from Dispose, or when the device goes away and the loop
		// finds itself with no sources left.
		while (!_disposed)
		{
			var reason = CFRunLoopRunInMode(mode, 0.25, false);
			if (reason == CFRunLoopRunResult.Finished || reason == CFRunLoopRunResult.Stopped)
			{
				break;
			}
		}

		IOHIDDeviceUnscheduleFromRunLoop(_device, _runLoop, mode);
		CFRelease(mode);
	}

	private void OnInputReport(nint context, int result, nint sender, int type, uint reportId, nint report, nint reportLength)
	{
		if (result != KIOReturnSuccess || reportLength <= 0)
		{
			return;
		}

		// Copy out: the buffer is reused for the next report the moment this returns.
		var length = Math.Min((int)reportLength, ColdcardUsb.InputReportLength);
		var frame = new byte[length];
		Array.Copy(_inputBuffer, frame, length);

		if (!_received.IsAddingCompleted)
		{
			_received.Add(frame);
		}
	}

	public void WriteReport(byte[] report65)
	{
		if (report65.Length != ColdcardUsb.OutputReportLength)
		{
			throw new ArgumentException($"Output report must be {ColdcardUsb.OutputReportLength} bytes.", nameof(report65));
		}

		// IOKit takes the report id separately, so send the frame without its leading id byte.
		var frame = report65[1..];
		var result = IOHIDDeviceSetReport(_device, KIOHIDReportTypeOutput, report65[0], frame, frame.Length);
		if (result != KIOReturnSuccess)
		{
			throw new IOException($"Coldcard HID write failed (IOKit error 0x{result:x8}).");
		}
	}

	public byte[]? ReadReport(int timeoutMs)
	{
		try
		{
			return _received.TryTake(out var frame, Math.Max(0, timeoutMs)) ? frame : null;
		}
		catch (ObjectDisposedException)
		{
			return null;
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;

		if (_runLoop != nint.Zero)
		{
			CFRunLoopStop(_runLoop);
		}

		_runLoopThread.Join(TimeSpan.FromSeconds(2));

		IOHIDDeviceClose(_device, 0);
		_received.CompleteAdding();
		_received.Dispose();
		_runLoopReady.Dispose();

		if (_inputBufferHandle.IsAllocated)
		{
			_inputBufferHandle.Free();
		}
	}

	private static nint CreateCfString(string value) =>
		CFStringCreateWithCString(nint.Zero, value, CFStringEncoding.Utf8);

	private enum CFStringEncoding : uint
	{
		Utf8 = 0x08000100,
	}

	private enum CFNumberType
	{
		SInt32 = 3,
	}

	private enum CFRunLoopRunResult
	{
		Finished = 1,
		Stopped = 2,
		TimedOut = 3,
		HandledSource = 4,
	}

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void IOHIDReportCallback(nint context, int result, nint sender, int type, uint reportId, nint report, nint reportLength);

	private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
	private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

	[DllImport(IOKit)]
	private static extern nint IOHIDManagerCreate(nint allocator, uint options);

	[DllImport(IOKit)]
	private static extern void IOHIDManagerSetDeviceMatching(nint manager, nint matching);

	[DllImport(IOKit)]
	private static extern nint IOHIDManagerCopyDevices(nint manager);

	[DllImport(IOKit)]
	private static extern int IOHIDDeviceOpen(nint device, uint options);

	[DllImport(IOKit)]
	private static extern int IOHIDDeviceClose(nint device, uint options);

	[DllImport(IOKit)]
	private static extern nint IOHIDDeviceGetProperty(nint device, nint key);

	[DllImport(IOKit)]
	private static extern int IOHIDDeviceSetReport(nint device, int reportType, nint reportId, byte[] report, nint reportLength);

	[DllImport(IOKit)]
	private static extern void IOHIDDeviceRegisterInputReportCallback(nint device, nint report, nint reportLength, IOHIDReportCallback callback, nint context);

	[DllImport(IOKit)]
	private static extern void IOHIDDeviceScheduleWithRunLoop(nint device, nint runLoop, nint runLoopMode);

	[DllImport(IOKit)]
	private static extern void IOHIDDeviceUnscheduleFromRunLoop(nint device, nint runLoop, nint runLoopMode);

	[DllImport(CoreFoundation)]
	private static extern void CFRelease(nint cf);

	[DllImport(CoreFoundation)]
	private static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string cStr, CFStringEncoding encoding);

	[DllImport(CoreFoundation)]
	private static extern bool CFStringGetCString(nint theString, byte[] buffer, nint bufferSize, CFStringEncoding encoding);

	[DllImport(CoreFoundation)]
	private static extern nint CFNumberCreate(nint allocator, CFNumberType theType, ref int valuePtr);

	[DllImport(CoreFoundation)]
	private static extern nint CFDictionaryCreateMutable(nint allocator, nint capacity, nint keyCallBacks, nint valueCallBacks);

	[DllImport(CoreFoundation)]
	private static extern void CFDictionarySetValue(nint theDict, nint key, nint value);

	[DllImport(CoreFoundation)]
	private static extern nint CFSetGetCount(nint theSet);

	[DllImport(CoreFoundation)]
	private static extern void CFSetGetValues(nint theSet, [Out] nint[] values);

	[DllImport(CoreFoundation)]
	private static extern nint CFRunLoopGetCurrent();

	[DllImport(CoreFoundation)]
	private static extern void CFRunLoopStop(nint runLoop);

	[DllImport(CoreFoundation)]
	private static extern CFRunLoopRunResult CFRunLoopRunInMode(nint mode, double seconds, [MarshalAs(UnmanagedType.I1)] bool returnAfterSourceHandled);
}
