using System.Collections.Generic;
using System.IO;

namespace WalletWasabi.Hwi.Coldcard;

/// <summary>
/// The Coldcard USB framing: a message is split across 64-byte HID reports. Each report's first byte is a
/// header — the low 6 bits are the payload length in that report, bit 0x80 marks the last report of a
/// message, and bit 0x40 marks an encrypted message. On the write side an extra leading report-id byte (0)
/// is prepended, so output reports are 65 bytes; input reports are 64 bytes.
/// </summary>
public static class CkccFraming
{
	private const int PayloadPerReport = 63;
	private const byte LastFlag = 0x80;
	private const byte EncryptFlag = 0x40;
	private const byte LengthMask = 0x3f;

	/// <summary>Splits a message into 65-byte output reports (report-id 0, header, up to 63 payload bytes).</summary>
	public static IEnumerable<byte[]> PackRequest(byte[] message, bool encrypted)
	{
		int offset = 0;
		do
		{
			int here = Math.Min(PayloadPerReport, message.Length - offset);
			var report = new byte[ColdcardUsb.OutputReportLength]; // [0]=report id 0
			bool last = offset + here == message.Length;
			report[1] = (byte)(here | (last ? LastFlag : 0) | (encrypted ? EncryptFlag : 0));
			Array.Copy(message, offset, report, 2, here);
			yield return report;
			offset += here;
		}
		while (offset < message.Length);
	}

	/// <summary>Reassembles a response by reading 64-byte input reports until the last-report flag is set.</summary>
	public static byte[] ReadResponse(Func<byte[]?> readReport)
	{
		var message = new List<byte>();
		while (true)
		{
			var report = readReport() ?? throw new IOException("Coldcard did not respond in time.");
			byte header = report[0];
			int length = header & LengthMask;
			for (int i = 0; i < length; i++)
			{
				message.Add(report[1 + i]);
			}

			if ((header & LastFlag) != 0)
			{
				return message.ToArray();
			}
		}
	}
}
