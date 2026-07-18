using System.Security.Cryptography;
using NBitcoin.Secp256k1;

namespace WalletWasabi.Hwi.Coldcard;

/// <summary>
/// The Coldcard USB link encryption. A fresh ECDH is done per connection: we send our ephemeral secp256k1
/// public key, the device replies with its own, and both derive the same session key
/// <c>SHA256(x ‖ y)</c> of the shared point. Traffic is then AES-256-CTR with the counter starting at zero,
/// with an independent counter in each direction. Uses NBitcoin's secp256k1 and .NET AES — no new dependency.
/// </summary>
public sealed class CkccEncryption
{
	private readonly ECPrivKey _ephemeralKey;
	private AesCtr? _encryptRequest;
	private AesCtr? _decryptResponse;

	public CkccEncryption()
	{
		Span<byte> secret = stackalloc byte[32];
		do
		{
			RandomNumberGenerator.Fill(secret);
		}
		while (!Context.Instance.TryCreateECPrivKey(secret, out _ephemeralKey!));
	}

	/// <summary>Our ephemeral public key as sent to the device: 64 bytes x ‖ y, no 0x04 prefix (ckcc wire format).</summary>
	public byte[] OurPublicKeyXY()
	{
		var buffer = new byte[65];
		_ephemeralKey.CreatePubKey().WriteToSpan(false, buffer, out _);
		return buffer[1..];
	}

	/// <summary>Given the device's public key (64 bytes, x ‖ y), derives the session key and arms AES-CTR.</summary>
	public void DeriveSessionKey(byte[] deviceXY)
	{
		if (deviceXY.Length != 64)
		{
			throw new ArgumentException("Device public key must be 64 bytes (x ‖ y).", nameof(deviceXY));
		}

		var uncompressed = new byte[65];
		uncompressed[0] = 0x04;
		deviceXY.CopyTo(uncompressed, 1);
		var devicePubKey = Context.Instance.CreatePubKey(uncompressed);

		// Shared point = ourPriv * devicePub; session key = SHA256(x ‖ y) of that point.
		var sharedPoint = devicePubKey.GetSharedPubkey(_ephemeralKey);
		var sharedUncompressed = new byte[65];
		sharedPoint.WriteToSpan(false, sharedUncompressed, out _);
		var sessionKey = System.Security.Cryptography.SHA256.HashData(sharedUncompressed.AsSpan(1)); // strip 0x04, hash x ‖ y

		_encryptRequest = new AesCtr(sessionKey);
		_decryptResponse = new AesCtr(sessionKey);
	}

	public bool IsArmed => _encryptRequest is not null;

	public byte[] EncryptRequest(byte[] plaintext) =>
		(_encryptRequest ?? throw new InvalidOperationException("Encryption not established.")).Process(plaintext);

	public byte[] DecryptResponse(byte[] ciphertext) =>
		(_decryptResponse ?? throw new InvalidOperationException("Encryption not established.")).Process(ciphertext);

	/// <summary>AES-256 in counter mode: a keystream from encrypting an incrementing 128-bit counter (from 0),
	/// XORed with the data. A single instance is used one direction, keeping counter state across messages.</summary>
	private sealed class AesCtr
	{
		private readonly Aes _aes;
		private readonly byte[] _counter = new byte[16];
		private readonly byte[] _keystreamBlock = new byte[16];
		private int _keystreamUsed = 16;

		public AesCtr(byte[] key)
		{
			_aes = Aes.Create();
			_aes.Mode = CipherMode.ECB;
			_aes.Padding = PaddingMode.None;
			_aes.Key = key;
		}

		public byte[] Process(byte[] data)
		{
			var output = new byte[data.Length];
			for (int i = 0; i < data.Length; i++)
			{
				if (_keystreamUsed == 16)
				{
					_aes.EncryptEcb(_counter, _keystreamBlock, PaddingMode.None);
					IncrementCounter();
					_keystreamUsed = 0;
				}
				output[i] = (byte)(data[i] ^ _keystreamBlock[_keystreamUsed++]);
			}
			return output;
		}

		private void IncrementCounter()
		{
			for (int i = 15; i >= 0; i--)
			{
				if (++_counter[i] != 0)
				{
					break;
				}
			}
		}
	}
}
