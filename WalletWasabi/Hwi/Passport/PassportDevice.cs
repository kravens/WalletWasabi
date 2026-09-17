using System.Buffers.Binary;
using System.IO;
using System.Text;
using NBitcoin;
using NBitcoin.DataEncoders;

namespace WalletWasabi.Hwi.Passport;

/// <summary>A connected Foundation Passport Prime driven over its wallet-rpc USB HID protocol (v2).</summary>
public sealed class PassportDevice : IPassportDevice
{
	public const uint CapOwnershipProofs = 1 << 0;
	public const uint CapCoinjoinSigning = 1 << 1;
	public const uint CapTaproot = 1 << 2;
	public const int TokenLength = 16;
	private const int SigningTimeoutMs = 120000;

	private readonly PassportTransport _transport;

	internal PassportDevice(PassportTransport transport)
	{
		_transport = transport;
	}

	public uint Capabilities { get; private set; }
	public string FirmwareVersion { get; private set; } = "";
	public bool SupportsTaproot => (Capabilities & CapTaproot) != 0;

	/// <summary>Opens the connected Passport (optionally pinned by serial) and reads its capabilities.</summary>
	public static PassportDevice Open(string? serialNumber = null)
	{
		var transport = new PassportTransport(PassportUsb.Open(serialNumber));
		try
		{
			var device = new PassportDevice(transport);
			device.ReadInfo();
			return device;
		}
		catch
		{
			transport.Dispose();
			throw;
		}
	}

	internal void ReadInfo()
	{
		var payload = _transport.SendReceive(PassportCommand.GetInfo, []);
		if (payload.Length < 5)
		{
			throw new PassportException("Short GetInfo response.");
		}
		if (payload[0] != PassportTransport.ProtocolVersion)
		{
			throw new PassportException($"The device speaks wallet-rpc protocol {payload[0]}, this wallet protocol {PassportTransport.ProtocolVersion}.");
		}
		Capabilities = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(1));
		FirmwareVersion = Encoding.UTF8.GetString(payload, 5, payload.Length - 5);

		if ((Capabilities & (CapOwnershipProofs | CapCoinjoinSigning)) != (CapOwnershipProofs | CapCoinjoinSigning))
		{
			throw new PassportException("Firmware does not advertise coinjoin remote-signing support.");
		}
	}

	public bool IsAlive()
	{
		try
		{
			ReadInfo();
			return true;
		}
		catch (Exception e) when (e is PassportException or IOException)
		{
			return false;
		}
	}

	public (HDFingerprint Fingerprint, ExtPubKey ExtPubKey) GetXpub(KeyPath keyPath, Network network)
	{
		using var request = new BinaryWriter(new MemoryStream());
		request.Write(NetworkByte(network));
		WritePath(request, keyPath);
		var response = _transport.SendReceive(PassportCommand.GetXpub, Bytes(request));
		if (response.Length < 5)
		{
			throw new PassportException("Short GetXpub response.");
		}

		// [fingerprint 4][xpub ascii]; any Base58 prefix is accepted, the network is the caller's business.
		var xpub = Encoding.ASCII.GetString(response, 4, response.Length - 4);
		return (new HDFingerprint(response[..4]), new ExtPubKey(Encoders.Base58Check.DecodeData(xpub)[4..]));
	}

	public byte[] AuthorizeCoinJoin(CoinjoinPolicy policy, int approvalTimeoutMs = 120000)
	{
		var token = _transport.SendReceive(PassportCommand.AuthorizeCoinjoin, policy.Serialize(), approvalTimeoutMs);
		if (token.Length != TokenLength)
		{
			throw new PassportException("AuthorizeCoinjoin did not return a session token.");
		}
		return token;
	}

	public byte[] GetOwnershipProof(byte[] sessionToken, KeyPath keyPath, byte[] commitmentData)
	{
		using var request = new BinaryWriter(new MemoryStream());
		request.Write(sessionToken);
		WritePath(request, keyPath);
		request.Write((ushort)commitmentData.Length);
		request.Write(commitmentData);
		return _transport.SendReceive(PassportCommand.GetOwnershipProof, Bytes(request));
	}

	public byte[] SignCoinJoin(byte[] sessionToken, byte[] psbt) =>
		_transport.SendReceive(PassportCommand.SignCoinjoin, [.. sessionToken, .. psbt], SigningTimeoutMs);

	public void RevokeSession(byte[] sessionToken) =>
		_transport.SendReceive(PassportCommand.RevokeSession, sessionToken);

	internal static byte NetworkByte(Network network) => network == Network.Main ? (byte)0 : (byte)1;

	/// <summary><c>[n u8][index u32 LE * n]</c>; BinaryWriter is little-endian by definition.</summary>
	private static void WritePath(BinaryWriter writer, KeyPath keyPath)
	{
		writer.Write((byte)keyPath.Indexes.Length);
		foreach (var index in keyPath.Indexes)
		{
			writer.Write(index);
		}
	}

	private static byte[] Bytes(BinaryWriter writer) => ((MemoryStream)writer.BaseStream).ToArray();

	public void Dispose() => _transport.Dispose();
}
