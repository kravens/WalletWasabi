using System.IO;
using System.Linq;
using System.Text;

namespace WalletWasabi.Hwi.Coldcard;

/// <summary>
/// One request/response conversation with a Coldcard: frames a message onto the HID channel, optionally
/// encrypts it, and reassembles the reply. A response is a 4-byte type tag (e.g. <c>asci</c>, <c>biny</c>,
/// <c>okay</c>, <c>err_</c>) followed by its payload.
/// </summary>
public sealed class ColdcardTransport : IDisposable
{
	private readonly IColdcardHid _hid;
	private CkccEncryption? _encryption;

	public ColdcardTransport(IColdcardHid hid)
	{
		_hid = hid;
	}

	/// <summary>Establishes AES link encryption; returns the device's (fingerprint, master xpub) from the reply.</summary>
	public (uint Fingerprint, string MasterXpub) StartEncryption()
	{
		var encryption = new CkccEncryption();

		// 'ncry' v1: our 65-byte uncompressed pubkey. Reply: his 64-byte pubkey ‖ 4-byte fingerprint ‖ xpub.
		var request = Encoding.ASCII.GetBytes("ncry").Concat(new byte[] { 0x01, 0, 0, 0 }).Concat(encryption.OurUncompressedPublicKey()).ToArray();
		var (tag, payload) = SendReceiveRaw(request, encrypt: false);
		if (tag != "mypb")
		{
			throw new IOException($"Unexpected reply to encrypt-start: '{tag}'.");
		}

		var deviceXY = payload[..64];
		uint fingerprint = BitConverter.ToUInt32(payload, 64);
		string masterXpub = Encoding.ASCII.GetString(payload, 68, payload.Length - 68).TrimEnd('\0');

		encryption.DeriveSessionKey(deviceXY);
		_encryption = encryption;
		return (fingerprint, masterXpub);
	}

	/// <summary>Sends a command and returns the (typed) response, encrypting once a session is established.</summary>
	public (string Tag, byte[] Payload) SendReceive(byte[] message, int timeoutMs = 15000) =>
		SendReceiveRaw(message, encrypt: _encryption is not null, timeoutMs);

	private (string Tag, byte[] Payload) SendReceiveRaw(byte[] message, bool encrypt, int timeoutMs = 15000)
	{
		byte[] framed = encrypt ? _encryption!.EncryptRequest(message) : message;
		foreach (var report in CkccFraming.PackRequest(framed, encrypt))
		{
			_hid.WriteReport(report);
		}

		byte[] response = CkccFraming.ReadResponse(() => _hid.ReadReport(timeoutMs));
		if (encrypt)
		{
			response = _encryption!.DecryptResponse(response);
		}

		if (response.Length < 4)
		{
			throw new IOException("Truncated Coldcard response.");
		}

		string tag = Encoding.ASCII.GetString(response, 0, 4);
		var payload = response[4..];
		if (tag == "err_")
		{
			throw new ColdcardException(Encoding.UTF8.GetString(payload));
		}
		return (tag, payload);
	}

	public void Dispose() => _hid.Dispose();
}

public class ColdcardException : Exception
{
	public ColdcardException(string message) : base("Coldcard: " + message)
	{
	}
}
