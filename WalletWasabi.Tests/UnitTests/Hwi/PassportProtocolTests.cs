using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NBitcoin;
using WalletWasabi.Hwi.Passport;
using WalletWasabi.Hwi.Usb;
using Xunit;

#pragma warning disable CA2000 // Dispose objects before losing scope - the fakes own nothing.

namespace WalletWasabi.Tests.UnitTests.Hwi;

/// <summary>
/// The Passport Prime wallet-rpc v2 client without a device: HID framing, the transport over a fake channel,
/// the policy wire format and command encoding, pinned with the vectors the firmware's own
/// <c>wallet-rpc-core</c> tests use.
/// </summary>
public class PassportProtocolTests
{
	private const uint H = 0x8000_0000;
	private static readonly byte[] Token = Enumerable.Repeat((byte)7, PassportDevice.TokenLength).ToArray();

	[Theory]
	[InlineData(0)]
	[InlineData(10)]
	[InlineData(59)]
	[InlineData(60)]
	[InlineData(4000)]
	[InlineData(200_000)]
	public void FramingRoundTrips(int length)
	{
		var message = RandomNumberGenerator.GetBytes(length);

		// Output reports are 65 bytes ([0]=report id, [1..]=64-byte body). The device sees the 64-byte body.
		var reports = PassportFraming.PackRequest(message).Select(r => r[1..]).ToList();
		var queue = new Queue<byte[]>(reports);
		var reassembled = PassportFraming.ReadResponse(() => queue.Count > 0 ? queue.Dequeue() : null);

		Assert.Equal(message, reassembled);
		Assert.Equal(length <= 59 ? 1 : 1 + (length - 59 + 60) / 61, reports.Count);
	}

	[Fact]
	public void AnOversizedFrameIsRefusedBeforeItIsAllocated()
	{
		var init = new byte[UsbHid.InputReportLength];
		BinaryPrimitives.WriteUInt32LittleEndian(init.AsSpan(1), PassportFraming.MaxFrameLen + 1);

		Assert.Throws<IOException>(() => PassportFraming.ReadResponse(() => init));
		Assert.Throws<ArgumentException>(() => PassportFraming.PackRequest(new byte[PassportFraming.MaxFrameLen + 1]).ToList());
	}

	[Fact]
	public void AnOutOfOrderContinuationIsRefused()
	{
		var reports = PassportFraming.PackRequest(new byte[200]).Select(r => r[1..]).ToList();
		(reports[1], reports[2]) = (reports[2], reports[1]);
		var queue = new Queue<byte[]>(reports);

		Assert.Throws<IOException>(() => PassportFraming.ReadResponse(() => queue.Dequeue()));
	}

	[Fact]
	public void SendReceiveFramesRequestAndParsesResponse()
	{
		using var hid = new FakeHid(_ => (PassportStatus.Ok, Encoding.ASCII.GetBytes("pong")));
		using var transport = new PassportTransport(hid);

		var response = transport.SendReceive(PassportCommand.GetInfo, [0xaa, 0xbb]);

		Assert.Equal("pong", Encoding.ASCII.GetString(response));
		// Request frame: [ver][cmd][len u32 LE][payload...]
		Assert.Equal(new byte[] { 2, PassportCommand.GetInfo, 2, 0, 0, 0, 0xaa, 0xbb }, hid.LastRequest);
	}

	[Fact]
	public void NonOkStatusThrowsAHardwareWalletException()
	{
		using var hid = new FakeHid(_ => (PassportStatus.Policy, []));
		using var transport = new PassportTransport(hid);

		var ex = Assert.Throws<PassportException>(() => transport.SendReceive(PassportCommand.SignCoinjoin, []));
		Assert.Contains("outside authorized policy", ex.Message);
		Assert.IsAssignableFrom<WalletWasabi.Wallets.HardwareWalletException>(ex);
	}

	[Fact]
	public void TheDeviceIsOpenedOnProtocolTwoOnly()
	{
		using var v1 = new FakeHid(_ => (PassportStatus.Ok, [1, 3, 0, 0, 0]));
		using var refusing = new FakeHid(_ => (PassportStatus.UnsupportedVersion, [2]));
		using var noTaproot = new FakeHid(_ => (PassportStatus.Ok, [2, 3, 0, 0, 0, (byte)'x']));
		using var full = new FakeHid(_ => (PassportStatus.Ok, [2, 7, 0, 0, 0]));

		Assert.Contains("protocol 1", Assert.Throws<PassportException>(() => OpenFakeDevice(v1, readInfo: true)).Message);
		Assert.Contains("unsupported protocol version", Assert.Throws<PassportException>(() => OpenFakeDevice(refusing, readInfo: true)).Message);
		var older = OpenFakeDevice(noTaproot, readInfo: true);
		Assert.False(older.SupportsTaproot);
		Assert.Equal("x", older.FirmwareVersion);
		Assert.True(OpenFakeDevice(full, readInfo: true).SupportsTaproot);
	}

	[Fact]
	public void PolicySerializationMatchesTheCrateVector()
	{
		var policy = new CoinjoinPolicy
		{
			Network = Network.Main,
			Account = 0,
			CoordinatorIdentifier = "CoinJoinCoordinatorIdentifier",
			FeeBudgetSats = 10_000,
			MaxRounds = 5,
			ValidForSeconds = 3600,
		};

		var coordinator = Encoding.ASCII.GetBytes("CoinJoinCoordinatorIdentifier");
		byte[] expected = [0, 0, 0, 0, 0, (byte)coordinator.Length, .. coordinator, 0x10, 0x27, 0, 0, 0, 0, 0, 0, 5, 0, 0x10, 0x0e, 0, 0];

		Assert.Equal(expected, policy.Serialize());
	}

	[Fact]
	public void GetXpubSendsTheCrateVectorAndReturnsTheDeviceFingerprint()
	{
		var xpub = TestKeyManagers.MasterKey.Derive(new KeyPath("84'/0'/0'")).Neuter().ToString(Network.Main);
		using var hid = new FakeHid(_ => (PassportStatus.Ok, [0x5c, 0x9e, 0x22, 0x8d, .. Encoding.ASCII.GetBytes(xpub)]));
		var device = OpenFakeDevice(hid);

		var (fingerprint, extPubKey) = device.GetXpub(new KeyPath("84'/0'/0'"), Network.Main);

		Assert.Equal([0, 3, 84, 0, 0, 0x80, 0, 0, 0, 0x80, 0, 0, 0, 0x80], hid.LastRequest[6..]);
		Assert.Equal(TestKeyManagers.MasterKey.Neuter().PubKey.GetHDFingerPrint(), fingerprint); // "all all all" is 5c9e228d
		Assert.Equal(xpub, extPubKey.ToString(Network.Main));
	}

	[Fact]
	public void EveryCommandOfASessionCarriesItsToken()
	{
		var requests = new List<byte[]>();
		using var hid = new FakeHid(_ => (PassportStatus.Ok, Token), requests.Add);
		var device = OpenFakeDevice(hid);
		var commitment = Encoding.ASCII.GetBytes("commit");

		var token = device.AuthorizeCoinJoin(new CoinjoinPolicy { Network = Network.Main, Account = 0, CoordinatorIdentifier = "c", FeeBudgetSats = 1, MaxRounds = 1, ValidForSeconds = 1 });
		device.GetOwnershipProof(token, new KeyPath("84'/0'/0'/1/0"), commitment);
		device.SignCoinJoin(token, [0x70, 0x73]);
		device.RevokeSession(token);

		Assert.Equal(Token, token);
		// [token 16][n u8][path u32*n][cd_len u16][cd]
		Assert.Equal([.. Token, 5, 84, 0, 0, 0x80, 0, 0, 0, 0x80, 0, 0, 0, 0x80, 1, 0, 0, 0, 0, 0, 0, 0, 6, 0, .. commitment], requests[1][6..]);
		Assert.Equal([.. Token, 0x70, 0x73], requests[2][6..]);
		Assert.Equal(Token, requests[3][6..]);
		Assert.Equal([PassportCommand.GetOwnershipProof, PassportCommand.SignCoinjoin, PassportCommand.RevokeSession], requests.Skip(1).Select(r => r[1]));
	}

	[Fact]
	public void AnAuthorizationWithoutAProperTokenIsRefused()
	{
		using var hid = new FakeHid(_ => (PassportStatus.Ok, [1, 2, 3, 4]));
		var device = OpenFakeDevice(hid);

		Assert.Throws<PassportException>(() => device.AuthorizeCoinJoin(new CoinjoinPolicy { Network = Network.Main, Account = 0, CoordinatorIdentifier = "c", FeeBudgetSats = 1, MaxRounds = 1, ValidForSeconds = 1 }));
	}

	/// <summary>A device over the fake channel; the GetInfo handshake only when the test is about it.</summary>
	private static PassportDevice OpenFakeDevice(FakeHid hid, bool readInfo = false)
	{
		var device = new PassportDevice(new PassportTransport(hid));
		if (readInfo)
		{
			device.ReadInfo();
		}
		return device;
	}

	/// <summary>A fake HID channel: buffers written reports, reassembles the request, and serves a canned reply.</summary>
	private sealed class FakeHid : IUsbHid
	{
		private readonly Action<byte[]>? _onRequest;
		private readonly List<byte[]> _written = new();
		private Queue<byte[]>? _responseReports;

		public FakeHid(Func<byte, (byte Status, byte[] Payload)> responder, Action<byte[]>? onRequest = null)
		{
			Responder = responder;
			_onRequest = onRequest;
		}

		public Func<byte, (byte Status, byte[] Payload)> Responder { get; }
		public byte[] LastRequest { get; private set; } = [];

		public void WriteReport(byte[] report65) => _written.Add(report65);

		public byte[]? ReadReport(int timeoutMs)
		{
			if (_responseReports is null)
			{
				// Reassemble the request from the written reports (strip the leading report-id byte).
				var queue = new Queue<byte[]>(_written.Select(r => r[1..]));
				_written.Clear();
				var request = PassportFraming.ReadResponse(() => queue.Count > 0 ? queue.Dequeue() : null);
				LastRequest = request;
				_onRequest?.Invoke(request);

				var (status, payload) = Responder(request[1]);
				byte[] response = [PassportTransport.ProtocolVersion, request[1], status, 0, 0, 0, 0, .. payload];
				BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(3), (uint)payload.Length);
				_responseReports = new Queue<byte[]>(PassportFraming.PackRequest(response).Select(r => r[1..]));
			}

			if (_responseReports.Count == 0)
			{
				_responseReports = null; // ready for the next exchange
				return null;
			}
			var report = _responseReports.Dequeue();
			if (_responseReports.Count == 0)
			{
				_responseReports = null;
			}
			return report;
		}

		public void Dispose()
		{
		}
	}
}
