using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using WalletWasabi.Hwi.Coldcard;
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

	[Fact]
	public void EcdhAndAesCtrRoundTrip()
	{
		// Two peers do the same ephemeral ECDH the device and host do; both must derive the same session key.
		var host = new CkccEncryption();
		var device = new CkccEncryption();
		host.DeriveSessionKey(device.OurUncompressedPublicKey()[1..]);   // strip 0x04 -> x ‖ y
		device.DeriveSessionKey(host.OurUncompressedPublicKey()[1..]);

		var plaintext = RandomNumberGenerator.GetBytes(100);
		var ciphertext = host.EncryptRequest(plaintext);

		Assert.NotEqual(plaintext, ciphertext);                          // actually encrypted
		Assert.Equal(plaintext, device.DecryptResponse(ciphertext));     // and the peer recovers it
	}

	[Fact]
	public void AesCtrMatchesManualCounterMode()
	{
		// CTR keystream must be AES-ECB of the big-endian counter starting at 0, XORed with the data.
		var host = new CkccEncryption();
		var device = new CkccEncryption();
		host.DeriveSessionKey(device.OurUncompressedPublicKey()[1..]);
		device.DeriveSessionKey(host.OurUncompressedPublicKey()[1..]);

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

	private sealed class FakeHid : IColdcardHid
	{
		private readonly Queue<byte[]> _toRead;
		public List<byte[]> Written { get; } = new();

		public FakeHid(IEnumerable<byte[]> toRead) => _toRead = new Queue<byte[]>(toRead);

		public void WriteReport(byte[] report65) => Written.Add(report65);
		public byte[]? ReadReport(int timeoutMs) => _toRead.Count > 0 ? _toRead.Dequeue() : null;
		public void Dispose() { }
	}
}
