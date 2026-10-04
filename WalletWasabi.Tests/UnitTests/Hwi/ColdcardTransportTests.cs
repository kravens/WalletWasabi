using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using WalletWasabi.Hwi.Coldcard;
using WalletWasabi.Hwi.Usb;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Hwi;

/// <summary>
/// Pure-logic tests for the Coldcard USB client (no device): the HID framing, the ECDH + AES-CTR link
/// encryption, and the request/response transport over a fake HID channel.
/// </summary>
public class ColdcardTransportTests
{
	[Theory]
	[InlineData(4)]
	[InlineData(63)]
	[InlineData(64)]
	[InlineData(126)]
	[InlineData(200)]
	public void FramingRoundTrips(int length)
	{
		var message = RandomNumberGenerator.GetBytes(length);

		// Write reports are 65 bytes ([0]=report id, [1]=header, [2..]=payload). The device sees 64-byte
		// input reports (header + payload), i.e. the write report minus its leading report-id byte.
		var inputReports = new Queue<byte[]>(CkccFraming.PackRequest(message, encrypted: false).Select(r => r[1..]));
		var reassembled = CkccFraming.ReadResponse(() => inputReports.Count > 0 ? inputReports.Dequeue() : null);

		Assert.Equal(message, reassembled);
	}

	// The reassembler runs against whatever the USB link hands back, so a wedged or malfunctioning device
	// must not send it past the end of a report or buffering forever.
	private static byte[] Report(byte[] payload, bool last)
	{
		var report = new byte[UsbHid.InputReportLength];
		report[0] = (byte)(payload.Length | (last ? 0x80 : 0));
		payload.CopyTo(report, 1);
		return report;
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

	[Fact]
	public void EcdhAndAesCtrRoundTrip()
	{
		// Two peers do the same ephemeral ECDH the device and host do; both must derive the same session key.
		using var host = new CkccEncryption();
		using var device = new CkccEncryption();
		host.DeriveSessionKey(device.OurPublicKeyXY());
		device.DeriveSessionKey(host.OurPublicKeyXY());

		var plaintext = RandomNumberGenerator.GetBytes(100);
		var ciphertext = host.EncryptRequest(plaintext);

		Assert.NotEqual(plaintext, ciphertext);                          // actually encrypted
		Assert.Equal(plaintext, device.DecryptResponse(ciphertext));     // and the peer recovers it
	}

	[Fact]
	public void AesCtrMatchesManualCounterMode()
	{
		// CTR keystream must be AES-ECB of the big-endian counter starting at 0, XORed with the data.
		using var host = new CkccEncryption();
		using var device = new CkccEncryption();
		host.DeriveSessionKey(device.OurPublicKeyXY());
		device.DeriveSessionKey(host.OurPublicKeyXY());

		// A zero plaintext returns the raw keystream, so the two directions produce the same first block.
		var zero = new byte[32];
		var keystreamA = host.EncryptRequest(zero);
		var keystreamB = device.DecryptResponse(zero);
		Assert.Equal(keystreamA, keystreamB);
	}

	[Fact]
	public void TransportParsesTaggedResponse()
	{
		// Fake HID that returns a framed, unencrypted "asci" + "hello" reply to any request.
		var reply = Encoding.ASCII.GetBytes("ascihello");
		using var fake = new FakeHid(CkccFraming.PackRequest(reply, encrypted: false).Select(r => r[1..]).ToList());

		using var transport = new ColdcardTransport(fake);
		var (tag, payload) = transport.SendReceive(Encoding.ASCII.GetBytes("vers"));

		Assert.Equal("asci", tag);
		Assert.Equal("hello", Encoding.ASCII.GetString(payload));
		Assert.NotEmpty(fake.Written);
	}

	// --- losing sync ---------------------------------------------------------------------------
	//
	// Each direction has its own AES-CTR counter and nothing ties a reply to its request, so one dropped
	// or late response leaves the two sides a message apart permanently. Seen on hardware: an upload
	// checksum "mismatch", then every ownership proof failing to parse until Wasabi was restarted. The
	// session cannot resynchronise, so it has to notice and be replaced rather than keep returning noise.

	private static IEnumerable<byte[]> Framed(string body) =>
		CkccFraming.PackRequest(Encoding.ASCII.GetBytes(body), encrypted: false).Select(r => r[1..]).ToList();

	private static IEnumerable<byte[]> FramedBytes(byte[] body) =>
		CkccFraming.PackRequest(body, encrypted: false).Select(r => r[1..]).ToList();

	[Fact]
	public void AnUnreadableReplyIsTreatedAsLostSync()
	{
		// What a desynchronised stream really yields: uniformly random bytes.
		using var fake = new FakeHid(FramedBytes([0x9f, 0x03, 0xe2, 0x7b, 0x11, 0x42]));
		using var transport = new ColdcardTransport(fake);

		var ex = Assert.Throws<ColdcardException>(() => transport.SendReceive(Encoding.ASCII.GetBytes("vers")));

		Assert.Contains("lost sync", ex.Message);
		Assert.False(transport.IsHealthy);
	}

	[Theory]
	[InlineData("int1")]   // every upload block; built with pack(), so easy to miss
	[InlineData("strx")]   // signing finished
	[InlineData("smrx")]   // message signing finished
	[InlineData("busy")]
	[InlineData("okay")]
	[InlineData("zzzz")]   // not a tag we know, but plainly a tag: pass it to the caller
	public void PlausibleTagsAreNotMistakenForLostSync(string tag)
	{
		// A list of known tags is the trap here: the first one missing from it turns a good reply into a
		// fake "lost sync" and breaks real work. int1 did exactly that and stopped uploads dead.
		using var fake = new FakeHid(Framed(tag + "body"));
		using var transport = new ColdcardTransport(fake);

		var (seen, _) = transport.SendReceive(Encoding.ASCII.GetBytes("vers"));

		Assert.Equal(tag, seen);
		Assert.True(transport.IsHealthy);
	}

	[Fact]
	public void ATimedOutExchangeMarksTheSessionUnusable()
	{
		// The abandoned reply can still turn up and be read as the answer to the next request.
		using var fake = new FakeHid([]);
		using var transport = new ColdcardTransport(fake);

		Assert.Throws<IOException>(() => transport.SendReceive(Encoding.ASCII.GetBytes("vers")));
		Assert.False(transport.IsHealthy);
	}

	[Fact]
	public void ADeviceErrorIsReportedWithoutCondemningTheSession()
	{
		// 'err_' is the device answering correctly. The link is fine and must not be torn down.
		using var fake = new FakeHid(Framed("err_nope"));
		using var transport = new ColdcardTransport(fake);

		var ex = Assert.Throws<ColdcardException>(() => transport.SendReceive(Encoding.ASCII.GetBytes("vers")));

		Assert.Contains("nope", ex.Message);
		Assert.True(transport.IsHealthy);
	}

	private sealed class FakeHid : IUsbHid
	{
		private readonly Queue<byte[]> _toRead;
		public List<byte[]> Written { get; } = new();

		public FakeHid(IEnumerable<byte[]> toRead) => _toRead = new Queue<byte[]>(toRead);

		public void WriteReport(byte[] report65) => Written.Add(report65);
		public byte[]? ReadReport(int timeoutMs) => _toRead.Count > 0 ? _toRead.Dequeue() : null;
		public void Dispose() { }
	}
}
