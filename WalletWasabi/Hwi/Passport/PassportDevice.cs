using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using NBitcoin;

namespace WalletWasabi.Hwi.Passport;

/// <summary>
/// A connected Foundation Passport Prime driven over its wallet-rpc USB HID protocol. Exposes the typed
/// commands the coinjoin flow needs: account xpub for import, a SLIP-19 ownership proof per input, a one-time
/// on-device coinjoin session authorization, and policy-enforced PSBT signing. Ownership proofs and
/// signatures are produced unattended once a session is authorized (see <see cref="AuthorizeCoinJoin"/>).
/// </summary>
public sealed class PassportDevice : IPassportDevice
{
	private readonly PassportTransport _transport;

	private PassportDevice(PassportTransport transport)
	{
		_transport = transport;
	}

	public byte ProtocolVersion { get; private set; }
	public uint Capabilities { get; private set; }
	public string FirmwareVersion { get; private set; } = "";

	public const uint CapOwnershipProofs = 1 << 0;
	public const uint CapCoinjoinSigning = 1 << 1;

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

	private void ReadInfo()
	{
		var payload = _transport.SendReceive(PassportCommand.GetInfo, []);
		if (payload.Length < 5)
		{
			throw new PassportException("Short GetInfo response.");
		}
		ProtocolVersion = payload[0];
		Capabilities = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(1));
		FirmwareVersion = System.Text.Encoding.ASCII.GetString(payload, 5, payload.Length - 5);

		if ((Capabilities & (CapOwnershipProofs | CapCoinjoinSigning)) != (CapOwnershipProofs | CapCoinjoinSigning))
		{
			throw new PassportException("Firmware does not advertise coinjoin remote-signing support.");
		}
	}

	/// <summary>Extended public key at the given derivation path (for wallet import).</summary>
	public BitcoinExtPubKey GetXpub(KeyPath keyPath, Network network)
	{
		var payload = EncodeNetworkAndPath(network, keyPath);
		var response = _transport.SendReceive(PassportCommand.GetXpub, payload);
		if (response.Length < 5)
		{
			throw new PassportException("Short GetXpub response.");
		}
		// [fingerprint 4][xpub ascii]; the fingerprint is informational (the xpub carries its own).
		var xpub = System.Text.Encoding.ASCII.GetString(response, 4, response.Length - 4);
		return new BitcoinExtPubKey(xpub, network);
	}

	/// <summary>
	/// Authorizes a coinjoin session on the device. The user reviews and approves the policy on the Passport
	/// screen; afterwards ownership proofs and signatures conforming to the policy are produced unattended.
	/// Returns the session id used by <see cref="GetOwnershipProof"/> and <see cref="SignCoinJoin"/>.
	/// </summary>
	public uint AuthorizeCoinJoin(CoinjoinPolicy policy, int approvalTimeoutMs = 120000)
	{
		var response = _transport.SendReceive(PassportCommand.AuthorizeCoinjoin, policy.Serialize(), approvalTimeoutMs);
		if (response.Length != 4)
		{
			throw new PassportException("AuthorizeCoinjoin did not return a session id.");
		}
		return BinaryPrimitives.ReadUInt32LittleEndian(response);
	}

	/// <summary>
	/// Produces a SLIP-19 ownership proof for a coinjoin input under an authorized session. The returned bytes
	/// are the fully serialized proof, ready for <c>OwnershipProof.FromBytes</c> and the coordinator.
	/// </summary>
	public byte[] GetOwnershipProof(uint sessionId, KeyPath keyPath, byte[] commitmentData)
	{
		var indexes = keyPath.Indexes;
		var payload = new List<byte>(4 + 1 + indexes.Length * 4 + 2 + commitmentData.Length);
		payload.AddRange(UInt32Le(sessionId));
		payload.Add((byte)indexes.Length);
		foreach (var index in indexes)
		{
			payload.AddRange(UInt32Le(index));
		}
		payload.AddRange(UInt16Le((ushort)commitmentData.Length));
		payload.AddRange(commitmentData);
		return _transport.SendReceive(PassportCommand.GetOwnershipProof, payload.ToArray());
	}

	/// <summary>
	/// Signs the coinjoin PSBT under an authorized session. The device verifies the round conforms to the
	/// policy (inputs ours, outputs self-spend, fee within the cap) and signs only our inputs, returning the
	/// updated PSBT. Rejected without a screen prompt if the round is out of policy.
	/// </summary>
	public byte[] SignCoinJoin(uint sessionId, byte[] psbt)
	{
		var payload = new byte[4 + psbt.Length];
		UInt32Le(sessionId).CopyTo(payload, 0);
		psbt.CopyTo(payload, 4);
		return _transport.SendReceive(PassportCommand.SignCoinjoin, payload);
	}

	/// <summary>Revokes a coinjoin session, disabling further unattended signing under it.</summary>
	public void RevokeSession(uint sessionId) =>
		_transport.SendReceive(PassportCommand.RevokeSession, UInt32Le(sessionId));

	private static byte[] EncodeNetworkAndPath(Network network, KeyPath keyPath)
	{
		var indexes = keyPath.Indexes;
		var payload = new byte[2 + indexes.Length * 4];
		payload[0] = NetworkByte(network);
		payload[1] = (byte)indexes.Length;
		for (int i = 0; i < indexes.Length; i++)
		{
			BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(2 + i * 4), indexes[i]);
		}
		return payload;
	}

	internal static byte NetworkByte(Network network) => network == Network.Main ? (byte)0 : (byte)1;

	private static byte[] UInt32Le(uint value)
	{
		var b = new byte[4];
		BinaryPrimitives.WriteUInt32LittleEndian(b, value);
		return b;
	}

	private static byte[] UInt16Le(ushort value)
	{
		var b = new byte[2];
		BinaryPrimitives.WriteUInt16LittleEndian(b, value);
		return b;
	}

	public void Dispose() => _transport.Dispose();
}
