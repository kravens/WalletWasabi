using System.Linq;
using WalletWasabi.Hwi.Usb;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Hwi;

public class UsbHidMacOsTests
{
	/// <summary>The matching dictionary must retain its keys and values: IOKit reads them after they were released, which segfaulted the process on the second or third enumeration.</summary>
	[Fact]
	public void EnumeratingRepeatedlyDoesNotCrash()
	{
		if (!OperatingSystem.IsMacOS())
		{
			return;
		}

		for (int i = 0; i < 300; i++)
		{
			Assert.Empty(UsbHid.Enumerate(0xd13e, 0xcc10, usagePage: null)); // Coldcard, not attached in CI.
			_ = UsbHid.Enumerate(0x1209, 0x5a52, usagePage: 0xff00).Count;
		}
	}
}
