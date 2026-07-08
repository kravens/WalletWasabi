using System.Buffers.Binary;
using System.Linq;
using System.Text;
using System.Threading;
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

	/// <summary>
	/// Installs an HSM policy (JSON) and enters HSM mode. The user reviews and approves the policy on the
	/// device; afterwards coinjoin PSBTs and ownership proofs are signed unattended within the policy.
	/// </summary>
	public void StartHsm(string policyJson, CancellationToken cancellationToken)
	{
		var data = Encoding.UTF8.GetBytes(policyJson);
		var sha = UploadFile(data);

		// 'hsms': length + sha of the uploaded policy. The device shows the policy for on-device approval.
		var request = new byte[40];
		Encoding.ASCII.GetBytes("hsms").CopyTo(request, 0);
		BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), (uint)data.Length);
		sha.CopyTo(request, 8);
		_transport.SendReceive(request, timeoutMs: 120000); // waits for the user to approve on the device
	}

	/// <summary>
	/// Signs a PSBT on the device (partial: only inputs this wallet owns) and returns the signed PSBT. Under
	/// an HSM policy this happens unattended; otherwise the device prompts for approval.
	/// </summary>
	public byte[] SignPsbt(byte[] psbt, CancellationToken cancellationToken)
	{
		var sha = UploadFile(psbt);

		// 'stxn': length + flags (0 = do not finalize, return signed PSBT) + sha.
		var request = new byte[40];
		Encoding.ASCII.GetBytes("stxn").CopyTo(request, 0);
		BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), (uint)psbt.Length);
		BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8), 0);
		sha.CopyTo(request, 12);
		_transport.SendReceive(request);

		var (length, resultSha) = PollForSignedFile(cancellationToken);
		return DownloadFile(length, resultSha, fileNumber: 1);
	}

	/// <summary>Uploads a file in blocks and verifies the device's checksum; returns its SHA-256.</summary>
	private byte[] UploadFile(byte[] data)
	{
		const int BlockSize = 1024;
		for (int offset = 0; offset < data.Length; offset += BlockSize)
		{
			int here = Math.Min(BlockSize, data.Length - offset);
			// 'upld' layout: tag(4) ‖ offset(u32) ‖ total_size(u32) ‖ data.
			var request = new byte[12 + here];
			Encoding.ASCII.GetBytes("upld").CopyTo(request, 0);
			BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), (uint)offset);
			BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8), (uint)data.Length);
			Array.Copy(data, offset, request, 12, here);
			_transport.SendReceive(request);
		}

		var expected = System.Security.Cryptography.SHA256.HashData(data);
		var (_, deviceSha) = _transport.SendReceive(Encoding.ASCII.GetBytes("sha2"));
		if (!deviceSha.AsSpan().SequenceEqual(expected))
		{
			throw new ColdcardException("Checksum mismatch during file upload.");
		}
		return expected;
	}

	/// <summary>Downloads a signed file (file 1) block by block and checks its SHA-256.</summary>
	private byte[] DownloadFile(uint length, byte[] expectedSha, int fileNumber)
	{
		const int BlockSize = 1024;
		var result = new byte[length];
		for (uint offset = 0; offset < length; offset += BlockSize)
		{
			uint here = Math.Min(BlockSize, length - offset);
			var request = new byte[16];
			Encoding.ASCII.GetBytes("dwld").CopyTo(request, 0);
			BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), offset);
			BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8), here);
			BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(12), (uint)fileNumber);
			var (_, chunk) = _transport.SendReceive(request);
			chunk.CopyTo(result, (int)offset);
		}

		if (!System.Security.Cryptography.SHA256.HashData(result).AsSpan().SequenceEqual(expectedSha))
		{
			throw new ColdcardException("Checksum mismatch during file download.");
		}
		return result;
	}

	private (uint Length, byte[] Sha) PollForSignedFile(CancellationToken cancellationToken)
	{
		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var (tag, payload) = _transport.SendReceive(Encoding.ASCII.GetBytes("stok"));
			if (tag == "strx") // done: <I32s> length + sha
			{
				return (BinaryPrimitives.ReadUInt32LittleEndian(payload), payload[4..36]);
			}
			// 'okay' (empty) means still working — poll again.
			Thread.Sleep(250);
		}
	}

	public void Dispose() => _transport.Dispose();
}
