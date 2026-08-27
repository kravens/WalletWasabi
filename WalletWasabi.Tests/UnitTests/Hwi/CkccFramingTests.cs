using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WalletWasabi.Hwi.Coldcard;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Hwi;

/// <summary>
/// The HID reassembler runs against whatever the USB link hands back, so it has to survive a device
/// that is wedged or malfunctioning rather than trusting the framing to terminate. These pin the two
/// ways it could otherwise fail: reading past the end of a truncated report, and buffering forever
/// when the last-report flag never arrives.
/// </summary>
public class CkccFramingTests
{
	private const byte LastFlag = 0x80;

	private static byte[] Report(byte[] payload, bool last)
	{
		var report = new byte[ColdcardUsb.InputReportLength];
		report[0] = (byte)(payload.Length | (last ? LastFlag : 0));
		payload.CopyTo(report, 1);
		return report;
	}

	[Fact]
	public void ReassemblesAMultiReportMessage()
	{
		var message = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
		var reports = new Queue<byte[]>([
			Report(message[..63], last: false),
			Report(message[63..], last: true),
		]);

		Assert.Equal(message, CkccFraming.ReadResponse(reports.Dequeue));
	}

	[Fact]
	public void RefusesATruncatedReport()
	{
		// Windows should always deliver a full report; a driver or device that does not must not send us
		// indexing off the end of the buffer mid-signing.
		var stunted = new byte[10];
		stunted[0] = 63; // claims 63 payload bytes it does not have

		var ex = Assert.Throws<IOException>(() => CkccFraming.ReadResponse(() => stunted));
		Assert.Contains("truncated", ex.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void StopsWhenTheMessageNeverEnds()
	{
		// A device that never sets the last-report flag would otherwise grow the buffer until the
		// process dies. Bound it by the protocol's own maximum message length instead.
		int served = 0;
		byte[] Endless()
		{
			served++;
			return Report(new byte[63], last: false);
		}

		var ex = Assert.Throws<IOException>(() => CkccFraming.ReadResponse(Endless));
		Assert.Contains("without ending", ex.Message);

		// Bounded, and bounded tightly: the cap is a couple of thousand bytes, not megabytes.
		Assert.InRange(served, 1, 100);
	}

	[Fact]
	public void TimeoutIsReportedRatherThanLoopingForever()
	{
		Assert.Throws<IOException>(() => CkccFraming.ReadResponse(() => null));
	}
}
