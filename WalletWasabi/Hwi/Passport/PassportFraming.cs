using System.Collections.Generic;
using System.IO;

namespace WalletWasabi.Hwi.Passport;

/// <summary>
/// Report framing for the Passport wallet-rpc HID protocol. A frame is split across 64-byte reports:
/// an init report <c>[0x00][len u16 LE][data …61]</c> followed by continuation reports
/// <c>[seq u8 (1..)][data …63]</c>. This mirrors the firmware's <c>frames.rs</c> reassembler exactly.
/// </summary>
internal static class PassportFraming
{
	private const int ReportLen = 64;
	private const byte InitMarker = 0x00;
	private const int InitDataLen = ReportLen - 3;
	private const int ContDataLen = ReportLen - 1;

	/// <summary>Splits a frame into 65-byte output reports (leading report-id 0 + 64-byte body).</summary>
	public static IEnumerable<byte[]> PackRequest(byte[] frame)
	{
		var init = new byte[PassportUsb.OutputReportLength];
		// init[0] = report id 0 (Windows HID prefix); body starts at init[1].
		init[1] = InitMarker;
		init[2] = (byte)(frame.Length & 0xff);
		init[3] = (byte)((frame.Length >> 8) & 0xff);
		int first = Math.Min(frame.Length, InitDataLen);
		Array.Copy(frame, 0, init, 4, first);
		yield return init;

		int offset = first;
		byte seq = 1;
		while (offset < frame.Length)
		{
			var report = new byte[PassportUsb.OutputReportLength];
			report[1] = seq++;
			int chunk = Math.Min(frame.Length - offset, ContDataLen);
			Array.Copy(frame, offset, report, 2, chunk);
			yield return report;
			offset += chunk;
		}
	}

	/// <summary>Reads reports via <paramref name="readReport"/> and reassembles one response frame.</summary>
	public static byte[] ReadResponse(Func<byte[]?> readReport)
	{
		var first = readReport() ?? throw new IOException("Passport response timed out.");
		if (first.Length < 3 || first[0] != InitMarker)
		{
			throw new IOException("Malformed Passport response init report.");
		}

		int expectedLen = first[1] | (first[2] << 8);
		var buffer = new byte[expectedLen];
		int copied = Math.Min(expectedLen, first.Length - 3);
		Array.Copy(first, 3, buffer, 0, copied);

		byte expectedSeq = 1;
		while (copied < expectedLen)
		{
			var report = readReport() ?? throw new IOException("Passport response truncated.");
			if (report.Length < 1 || report[0] != expectedSeq)
			{
				throw new IOException($"Out-of-order Passport response report (want seq {expectedSeq}).");
			}
			expectedSeq++;
			int chunk = Math.Min(expectedLen - copied, report.Length - 1);
			Array.Copy(report, 1, buffer, copied, chunk);
			copied += chunk;
		}

		return buffer;
	}
}
