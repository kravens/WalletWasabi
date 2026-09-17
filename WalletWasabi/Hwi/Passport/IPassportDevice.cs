using NBitcoin;

namespace WalletWasabi.Hwi.Passport;

/// <summary>
/// The typed wallet-rpc surface of a Passport Prime coinjoin remote signer, independent of how the device is
/// reached: <see cref="PassportDevice"/> implements it over USB HID, and a QuantumLink client can implement the
/// same surface. An implementation verifies at connect time that the firmware speaks this protocol version and
/// advertises ownership proofs and coinjoin signing, and never keeps a payload beyond the exchange: requests
/// carry derivation paths and PSBTs that reveal wallet contents.
/// </summary>
public interface IPassportDevice : IDisposable
{
	uint Capabilities { get; }
	string FirmwareVersion { get; }

	/// <summary>Whether the firmware signs taproot inputs; a coin it cannot sign must stay out of rounds.</summary>
	bool SupportsTaproot { get; }

	/// <summary>Whether the device still answers on this connection, so an authorized session can be carried on with.</summary>
	bool IsAlive();

	/// <summary>The account key at a path, with the master fingerprint the device computed for it.</summary>
	(HDFingerprint Fingerprint, ExtPubKey ExtPubKey) GetXpub(KeyPath keyPath, Network network);

	/// <summary>Has the user approve a session policy on the device and returns the random 16-byte session token every later command carries.</summary>
	byte[] AuthorizeCoinJoin(CoinjoinPolicy policy, int approvalTimeoutMs = 120000);

	/// <summary>A serialized SLIP-19 ownership proof for one of our inputs, produced unattended under the session.</summary>
	byte[] GetOwnershipProof(byte[] sessionToken, KeyPath keyPath, byte[] commitmentData);

	/// <summary>The PSBT with our inputs signed, after the device checked the round against the session policy; refused without a prompt when it is out of policy.</summary>
	byte[] SignCoinJoin(byte[] sessionToken, byte[] psbt);

	/// <summary>Ends the session; the device zeroizes its keys and refuses the token from then on.</summary>
	void RevokeSession(byte[] sessionToken);
}
