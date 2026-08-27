using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NBitcoin;
using WalletWasabi.Hwi.Passport;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Hwi;

/// <summary>
/// Pure-logic tests for the Passport Prime wallet-rpc client (no device): the HID framing reassembler, the
/// request/response transport over a fake HID channel, the policy wire format, and command encoding. These
/// pin the wire contract shared with the firmware's <c>wallet-rpc-core</c> crate.
/// </summary>
public class PassportProtocolTests
{
	[Theory]
	[InlineData(4)]
	[InlineData(61)]
	[InlineData(62)]
	[InlineData(200)]
	[InlineData(4000)]
	public void FramingRoundTrips(int length)
	{
		var message = RandomNumberGenerator.GetBytes(length);

		// Output reports are 65 bytes ([0]=report id, [1..]=64-byte body). The device sees the 64-byte body.
		var inputReports = new Queue<byte[]>(PassportFraming.PackRequest(message).Select(r => r[1..]));
		var reassembled = PassportFraming.ReadResponse(() => inputReports.Count > 0 ? inputReports.Dequeue() : null);

		Assert.Equal(message, reassembled);
	}

	[Fact]
	public void SendReceiveFramesRequestAndParsesResponse()
	{
		// A fake device that echoes an OK response carrying a known payload, and records the request it saw.
		using var hid = new FakeHid(command => (PassportStatus.Ok, Encoding.ASCII.GetBytes("pong")));
		using var transport = new PassportTransport(hid);

		var response = transport.SendReceive(PassportCommand.GetInfo, [0xaa, 0xbb]);

		Assert.Equal("pong", Encoding.ASCII.GetString(response));
		// Request frame: [ver][cmd][len lo][len hi][payload...]
		Assert.Equal(PassportTransport.ProtocolVersion, hid.LastRequest[0]);
		Assert.Equal(PassportCommand.GetInfo, hid.LastRequest[1]);
		Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(hid.LastRequest.AsSpan(2)));
		Assert.Equal(new byte[] { 0xaa, 0xbb }, hid.LastRequest[4..6]);
	}

	[Fact]
	public void NonOkStatusThrows()
	{
		using var hid = new FakeHid(_ => (PassportStatus.Policy, []));
		using var transport = new PassportTransport(hid);

		var ex = Assert.Throws<PassportException>(() => transport.SendReceive(PassportCommand.SignCoinjoin, []));
		Assert.Contains("outside authorized policy", ex.Message);
	}

	[Fact]
	public void LargeRequestSpansMultipleReports()
	{
		using var hid = new FakeHid(_ => (PassportStatus.Ok, []));
		using var transport = new PassportTransport(hid);

		// A PSBT-sized payload must be split across many 64-byte reports and still parse a valid reply.
		transport.SendReceive(PassportCommand.SignCoinjoin, RandomNumberGenerator.GetBytes(2000));

		Assert.True(hid.WrittenReportCount > 30, $"expected multi-report write, got {hid.WrittenReportCount}");
	}

	[Fact]
	public void PolicySerializationMatchesWireFormat()
	{
		var policy = new CoinjoinPolicy
		{
			Network = Network.Main,
			Account = 0,
			CoordinatorIdentifier = "CoinJoinCoordinatorIdentifier",
			MaxFeeContributionSats = 10_000,
			MaxRounds = 5,
			ValidForSeconds = 3600,
		};

		var bytes = policy.Serialize();
		var coordinator = Encoding.ASCII.GetBytes("CoinJoinCoordinatorIdentifier");

		int offset = 0;
		Assert.Equal(0, bytes[offset++]); // mainnet
		Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset))); offset += 4;
		Assert.Equal(coordinator.Length, bytes[offset++]);
		Assert.Equal(coordinator, bytes[offset..(offset + coordinator.Length)]); offset += coordinator.Length;
		Assert.Equal(10_000ul, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset))); offset += 8;
		Assert.Equal(5, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset))); offset += 2;
		Assert.Equal(3600u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset))); offset += 4;
		Assert.Equal(bytes.Length, offset);
	}

	[Fact]
	public void OwnershipProofRequestEncodesSessionPathAndCommitment()
	{
		byte[]? captured = null;
		using var hid = new FakeHid(_ => (PassportStatus.Ok, [0x53, 0x4c, 0x00, 0x19]), req => captured = req);
		using var transport = new PassportTransport(hid);
		var device = OpenFakeDevice(transport);

		var keyPath = new KeyPath("84'/0'/0'/1/0");
		var commitment = Encoding.ASCII.GetBytes("commit");
		device.GetOwnershipProof(sessionId: 7, keyPath, commitment);

		// payload = [session u32][n u8][path u32*n][cd_len u16][cd]
		var payload = captured![4..];
		Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(payload));
		Assert.Equal(5, payload[4]); // 5 path elements
		int cdLenOffset = 4 + 1 + 5 * 4;
		Assert.Equal(commitment.Length, BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(cdLenOffset)));
		Assert.Equal(commitment, payload[(cdLenOffset + 2)..]);
	}

	private static PassportDevice OpenFakeDevice(PassportTransport transport)
	{
		// PassportDevice.Open needs real USB; construct via the internal test path by invoking the private
		// constructor through reflection is avoided — instead the transport-level tests above cover framing,
		// and this helper builds a device that skips the GetInfo handshake for request-encoding assertions.
		var ctor = typeof(PassportDevice).GetConstructor(
			System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
			binder: null, [typeof(PassportTransport)], modifiers: null);
		return (PassportDevice)ctor!.Invoke([transport]);
	}

	/// <summary>A fake HID channel: buffers written reports, reassembles the request, and serves a canned reply.</summary>
	private sealed class FakeHid : IPassportHid
	{
		private readonly Func<byte, (byte Status, byte[] Payload)> _responder;
		private readonly Action<byte[]>? _onRequest;
		private readonly List<byte[]> _written = new();
		private Queue<byte[]>? _responseReports;

		public FakeHid(Func<byte, (byte Status, byte[] Payload)> responder, Action<byte[]>? onRequest = null)
		{
			_responder = responder;
			_onRequest = onRequest;
		}

		public byte[] LastRequest { get; private set; } = [];
		public int WrittenReportCount => _written.Count;

		public void WriteReport(byte[] report65)
		{
			_written.Add(report65);
		}

		public byte[]? ReadReport(int timeoutMs)
		{
			if (_responseReports is null)
			{
				// Reassemble the request from the written reports (strip the leading report-id byte).
				var request = PassportFraming.ReadResponse(NextWrittenBody());
				LastRequest = request;
				_onRequest?.Invoke(request);

				var (status, payload) = _responder(request[1]);
				var response = new byte[5 + payload.Length];
				response[0] = PassportTransport.ProtocolVersion;
				response[1] = request[1];
				response[2] = status;
				BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(3), (ushort)payload.Length);
				payload.CopyTo(response, 5);
				_responseReports = new Queue<byte[]>(PassportFraming.PackRequest(response).Select(r => r[1..]));
			}

			return _responseReports.Count > 0 ? _responseReports.Dequeue() : null;
		}

		private Func<byte[]?> NextWrittenBody()
		{
			var queue = new Queue<byte[]>(_written.Select(r => r[1..]));
			return () => queue.Count > 0 ? queue.Dequeue() : null;
		}

		public void Dispose()
		{
		}
	}
}
