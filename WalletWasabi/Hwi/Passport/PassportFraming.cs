using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using WalletWasabi.Hwi.Usb;

namespace WalletWasabi.Hwi.Passport;

/// <summary>
/// Report framing for the Passport wallet-rpc HID protocol, mirroring the firmware's <c>frames.rs</c>. A frame
/// is split across 64-byte reports: an init report <c>[0x00][len u32 LE][data …59]</c> followed by continuation
/// reports <c>[0x01][seq u16 LE from 1][data …61]</c>.
/// </summary>
internal static class PassportFraming
{
	private const byte InitMarker = 0x00;
	private const byte ContMarker = 0x01;
	private const int InitDataLen = UsbHid.InputReportLength - 5;
	private const int ContDataLen = UsbHid.InputReportLength - 3;

	/// <summary>The biggest PSBT the device accepts plus its header; a longer declared length is refused before anything is allocated.</summary>
	public const int MaxFrameLen = 512 * 1024 + 1024;

	/// <summary>Splits a frame into 65-byte output reports (leading report-id 0 + 64-byte body).</summary>
	public static IEnumerable<byte[]> PackRequest(byte[] frame)
	{
		if (frame.Length > MaxFrameLen)
		{
			throw new ArgumentException($"A frame carries at most {MaxFrameLen} bytes.", nameof(frame));
		}

		var init = new byte[UsbHid.OutputReportLength];
		init[1] = InitMarker;
		BinaryPrimitives.WriteUInt32LittleEndian(init.AsSpan(2), (uint)frame.Length);
		int first = Math.Min(frame.Length, InitDataLen);
		Array.Copy(frame, 0, init, 6, first);
		yield return init;

		int offset = first;
		ushort seq = 1;
		while (offset < frame.Length)
		{
			var report = new byte[UsbHid.OutputReportLength];
			report[1] = ContMarker;
			BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(2), seq++);
			int chunk = Math.Min(frame.Length - offset, ContDataLen);
			Array.Copy(frame, offset, report, 4, chunk);
			yield return report;
			offset += chunk;
		}
	}

	/// <summary>Reads reports via <paramref name="readReport"/> and reassembles one response frame.</summary>
	public static byte[] ReadResponse(Func<byte[]?> readReport)
	{
		var first = readReport() ?? throw new IOException("Passport response timed out.");
		if (first.Length < 5 || first[0] != InitMarker)
		{
			throw new IOException("Malformed Passport response init report.");
		}

		uint declared = BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(1));
		if (declared > MaxFrameLen)
		{
			throw new IOException($"Passport response declares {declared} bytes, more than a frame may carry.");
		}

		int expectedLen = (int)declared;
		var buffer = new byte[expectedLen];
		int copied = Math.Min(expectedLen, first.Length - 5);
		Array.Copy(first, 5, buffer, 0, copied);

		ushort expectedSeq = 1;
		while (copied < expectedLen)
		{
			var report = readReport() ?? throw new IOException("Passport response truncated.");
			if (report.Length < 3 || report[0] != ContMarker || BinaryPrimitives.ReadUInt16LittleEndian(report.AsSpan(1)) != expectedSeq)
			{
				throw new IOException($"Out-of-order Passport response report (want seq {expectedSeq}).");
			}
			expectedSeq++;
			int chunk = Math.Min(expectedLen - copied, report.Length - 3);
			Array.Copy(report, 3, buffer, copied, chunk);
			copied += chunk;
		}

		return buffer;
	}
}
