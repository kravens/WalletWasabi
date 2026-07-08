using System.Buffers.Binary;
using System.Linq;
using System.Text;
using NBitcoin;

namespace WalletWasabi.Hwi.Coldcard;

/// <summary>
/// A connected Coldcard, driven over its raw USB protocol (no bridge daemon). Opens the HID channel,
/// establishes AES link encryption, and exposes the typed commands the coinjoin flow needs. Ownership
/// proofs use the <c>slp9</c> command added by the accompanying firmware change; PSBT signing under an HSM
/// policy is layered on top for the key chain.
/// </summary>
public sealed class ColdcardDevice : IDisposable
{
	private readonly ColdcardTransport _transport;

	private ColdcardDevice(ColdcardTransport transport)
	{
		_transport = transport;
	}

	public uint MasterFingerprint { get; private set; }
	public string MasterXpub { get; private set; } = "";

	/// <summary>Opens the connected Coldcard (optionally pinned by serial) and establishes encryption.</summary>
	public static ColdcardDevice Open(string? serialNumber = null)
	{
		var transport = new ColdcardTransport(ColdcardUsb.Open(serialNumber));
		try
		{
			var device = new ColdcardDevice(transport);
			var (fingerprint, masterXpub) = transport.StartEncryption();
			device.MasterFingerprint = fingerprint;
			device.MasterXpub = masterXpub;
			return device;
		}
		catch
		{
			transport.Dispose();
			throw;
		}
	}

	/// <summary>Multi-line version string (firmware version, git hash, model, etc.).</summary>
	public string GetVersion()
	{
		var (_, payload) = _transport.SendReceive(Encoding.ASCII.GetBytes("vers"));
		return Encoding.ASCII.GetString(payload);
	}

	/// <summary>Extended public key at the given derivation path.</summary>
	public BitcoinExtPubKey GetXpub(KeyPath keyPath, Network network)
	{
		var request = Encoding.ASCII.GetBytes("xpub" + $"m/{keyPath}");
		var (_, payload) = _transport.SendReceive(request);
		return new BitcoinExtPubKey(Encoding.ASCII.GetString(payload).TrimEnd('\0'), network);
	}

	/// <summary>
	/// Produces a SLIP-19 ownership proof for a coinjoin input (firmware <c>slp9</c> command). The returned
	/// bytes are the fully serialized proof, ready for <c>OwnershipProof.FromBytes</c> and the coordinator.
	/// </summary>
	public byte[] SignOwnershipProof(KeyPath keyPath, byte[] commitmentData, bool userConfirmation = true)
	{
		var subpath = Encoding.ASCII.GetBytes($"m/{keyPath}");
		byte flags = (byte)(userConfirmation ? 0x01 : 0x00);

		var header = new byte[16];
		Encoding.ASCII.GetBytes("slp9").CopyTo(header, 0);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), flags);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)subpath.Length);
		BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)commitmentData.Length);

		var request = header.Concat(subpath).Concat(commitmentData).ToArray();
		var (_, payload) = _transport.SendReceive(request);
		return payload;
	}

	public void Dispose() => _transport.Dispose();
}
