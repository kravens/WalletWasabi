using System;
using System.IO;
using System.Linq;
using WalletWasabi.Hwi.Usb;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Hwi;

/// <summary>
/// The Linux transport finds devices by parsing <c>/sys/class/hidraw/*/device/uevent</c>, and that parsing
/// is where it can quietly be wrong: the vendor and product arrive as zero-padded hex inside a
/// colon-separated field, and unrelated HID devices sit in the same directory.
/// A real device cannot cover this in CI, so the sysfs root is injectable and the tree is faked here.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
	"Interoperability",
	"CA1416:Validate platform compatibility",
	Justification = "The enumerator is marked linux-only because that is where it is useful, but it is "
		+ "plain Directory/File reads over an injected root, so it runs anywhere. Restricting these tests "
		+ "to Linux would mean never running them.")]
public class UsbHidLinuxTests : IDisposable
{
	private const ushort VendorId = 0xd13e; // A Coldcard.
	private const ushort ProductId = 0xcc10;

	private readonly string _root = Path.Combine(Path.GetTempPath(), "hidraw-test-" + Guid.NewGuid().ToString("N"));

	/// <summary>Writes what the enumerator reads: <c>&lt;root&gt;/hidrawN/device/uevent</c>.</summary>
	private void FakeDevice(string node, string? hidId, string? serial = null)
	{
		var dir = Path.Combine(_root, node, "device");
		Directory.CreateDirectory(dir);

		var lines = new System.Collections.Generic.List<string> { "DRIVER=hid-generic" };
		if (hidId is not null)
		{
			lines.Add($"HID_ID={hidId}");
		}
		lines.Add("HID_NAME=Coinkite Coldcard Wallet");
		if (serial is not null)
		{
			lines.Add($"HID_UNIQ={serial}");
		}

		File.WriteAllLines(Path.Combine(dir, "uevent"), lines);
	}

	[Fact]
	public void FindsTheColdcard()
	{
		// The real thing: bus 0003, vendor d13e, product cc10, zero-padded to eight digits.
		FakeDevice("hidraw3", "0003:0000D13E:0000CC10", "2050395F4833");

		var found = UsbHidLinux.EnumerateDevices(VendorId, ProductId, classRoot: _root).ToList();

		Assert.Equal("/dev/hidraw3", Assert.Single(found));
	}

	[Fact]
	public void IgnoresEveryOtherHidDevice()
	{
		// A keyboard, a mouse and a device with no HID_ID at all, which is what a real machine looks like.
		FakeDevice("hidraw0", "0003:00001532:000000B7", "razer");
		FakeDevice("hidraw1", "0003:0000048D:00005702");
		FakeDevice("hidraw2", null);
		FakeDevice("hidraw4", "0003:0000D13E:0000CC10", "2050395F4833");

		var found = UsbHidLinux.EnumerateDevices(VendorId, ProductId, classRoot: _root).ToList();

		Assert.Equal("/dev/hidraw4", Assert.Single(found));
	}

	[Fact]
	public void AMalformedHidIdIsNotAMatch()
	{
		// Truncated, non-hex, and the vendor/product the right value in the wrong field. None should pass,
		// and none should throw: this runs over whatever the kernel happens to expose.
		FakeDevice("hidraw0", "0003:0000D13E");
		FakeDevice("hidraw1", "0003:zzzzzzzz:0000CC10");
		FakeDevice("hidraw2", "0000D13E:0000CC10:0003");
		FakeDevice("hidraw3", "");

		Assert.Empty(UsbHidLinux.EnumerateDevices(VendorId, ProductId, classRoot: _root));
	}

	[Fact]
	public void AMissingSysfsIsNotAnError()
	{
		// Windows, macOS, or a kernel without HID support — as WSL2's stock kernel is.
		Assert.Empty(UsbHidLinux.EnumerateDevices(VendorId, ProductId, classRoot: Path.Combine(_root, "does-not-exist")));
	}

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}

		GC.SuppressFinalize(this);
	}
}
