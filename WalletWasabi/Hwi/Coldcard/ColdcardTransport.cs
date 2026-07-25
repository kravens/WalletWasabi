using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using WalletWasabi.Logging;

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

	/// <summary>
	/// False once this session can no longer be trusted. Request and response each have their own AES-CTR
	/// counter and nothing correlates a reply to its request, so a single dropped or late response leaves
	/// the two sides permanently one message apart — every later reply then decrypts to garbage. Seen on
	/// hardware: an upload checksum "mismatch" followed by every ownership proof failing to parse. There is
	/// no way to resynchronise, so the session has to be thrown away and a new one established.
	/// </summary>
	public bool IsHealthy { get; private set; } = true;

	/// <summary>Records that a reply did not make sense, even though it parsed — a checksum that cannot be
	/// right is the same evidence of lost sync as an unknown tag.</summary>
	public void MarkUnhealthy() => IsHealthy = false;

	/// <summary>Establishes AES link encryption; returns the device's (fingerprint, master xpub) from the reply.</summary>
	public (uint Fingerprint, string MasterXpub) StartEncryption()
	{
		var encryption = new CkccEncryption();

		// 'ncry' v1: our 64-byte x ‖ y pubkey (no 0x04 prefix). Reply: his 64-byte pubkey ‖ 4-byte fingerprint ‖ xpub.
		var request = Encoding.ASCII.GetBytes("ncry").Concat(new byte[] { 0x01, 0, 0, 0 }).Concat(encryption.OurPublicKeyXY()).ToArray();
		var (tag, payload) = SendReceiveRaw(request, encrypt: false);
		if (tag != "mypb")
		{
			throw new IOException($"Unexpected reply to encrypt-start: '{tag}'.");
		}

		// mypb layout: device pubkey (64) ‖ fingerprint (u32) ‖ xpub length (u32) ‖ xpub.
		var deviceXY = payload[..64];
		uint fingerprint = BitConverter.ToUInt32(payload, 64);
		uint xpubLength = BitConverter.ToUInt32(payload, 68);
		string masterXpub = xpubLength == 0 ? "" : Encoding.ASCII.GetString(payload, 72, (int)xpubLength);

		encryption.DeriveSessionKey(deviceXY);
		_encryption = encryption;
		return (fingerprint, masterXpub);
	}

	/// <summary>Sends a command and returns the (typed) response, encrypting once a session is established.</summary>
	public (string Tag, byte[] Payload) SendReceive(byte[] message, int timeoutMs = 15000) =>
		SendReceiveRaw(message, encrypt: _encryption is not null, timeoutMs);

	private (string Tag, byte[] Payload) SendReceiveRaw(byte[] message, bool encrypt, int timeoutMs = 15000)
	{
		var command = message.Length >= 4 ? Encoding.ASCII.GetString(message, 0, 4) : "????";
		var startedAt = DateTimeOffset.UtcNow;

		byte[] response;
		try
		{
			byte[] framed = encrypt ? _encryption!.EncryptRequest(message) : message;
			foreach (var report in CkccFraming.PackRequest(framed, encrypt))
			{
				_hid.WriteReport(report);
			}

			response = CkccFraming.ReadResponse(() => _hid.ReadReport(timeoutMs));
		}
		catch (IOException e)
		{
			// A half-finished exchange is exactly what puts the two counters out of step, and the reply we
			// gave up on may still arrive to be misread as the answer to the next request.
			IsHealthy = false;
			Logger.LogDebug(
				$"ckcc '{command}' ({message.Length} B) failed after "
				+ $"{(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds:0} ms: {e.Message}");
			throw;
		}

		if (encrypt)
		{
			response = _encryption!.DecryptResponse(response);
		}

		if (response.Length < 4)
		{
			IsHealthy = false;
			throw new IOException("Truncated Coldcard response.");
		}

		string tag = Encoding.ASCII.GetString(response, 0, 4);
		Logger.LogDebug(
			$"ckcc '{command}' ({message.Length} B) -> '{tag}' ({response.Length} B) in "
			+ $"{(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds:0} ms");

		if (!LooksLikeATag(response))
		{
			IsHealthy = false;
			Logger.LogDebug($"ckcc unreadable reply, first 16 bytes: {Convert.ToHexString(response[..Math.Min(16, response.Length)])}");
			throw new ColdcardException(
				"the reply could not be read, so the encrypted link has lost sync. Reconnecting to the device.");
		}

		var payload = response[4..];
		if (tag == "err_")
		{
			throw new ColdcardException(Encoding.UTF8.GetString(payload));
		}
		return (tag, payload);
	}

	/// <summary>
	/// Whether a reply even looks like a reply. Every firmware tag is four lower-case ASCII characters
	/// (<c>asci</c>, <c>biny</c>, <c>okay</c>, <c>err_</c>, <c>int1</c>, <c>strx</c>, …), while a reply read
	/// off a desynchronised AES-CTR stream is uniformly random bytes and matches this about once in 2500.
	/// <para>
	/// Deliberately a character test and not a list of known tags. A list is the obvious implementation and
	/// it is a trap: the first tag missing from it turns a perfectly good reply into a fake "lost sync" and
	/// breaks real work. That happened here — <c>int1</c>, the reply to every upload block, is assembled
	/// with <c>pack()</c> rather than written as a literal, so it was missed and uploads stopped working.
	/// Anything plausible is passed through for the caller to judge; only clear binary noise is rejected.
	/// </para>
	/// </summary>
	private static bool LooksLikeATag(byte[] response)
	{
		for (int i = 0; i < 4; i++)
		{
			byte b = response[i];
			bool ok = (b >= 'a' && b <= 'z') || (b >= '0' && b <= '9') || b == '_';
			if (!ok)
			{
				return false;
			}
		}

		return true;
	}

	public void Dispose() => _hid.Dispose();
}

public class ColdcardException : Exception
{
	public ColdcardException(string message) : base("Coldcard: " + message)
	{
	}
}
