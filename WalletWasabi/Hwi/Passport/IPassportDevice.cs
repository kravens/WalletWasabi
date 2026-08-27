using NBitcoin;

namespace WalletWasabi.Hwi.Passport;

/// <summary>
/// The typed wallet-rpc surface of a Passport Prime coinjoin remote signer, independent of how the device is
/// reached. <see cref="PassportDevice"/> implements it over USB HID; a QuantumLink (BLE) client implements the
/// same surface once Foundation adds the coinjoin messages to the protocol — the coinjoin flow
/// (<see cref="WabiSabi.Client.PassportKeyChain"/>) is transport-agnostic through this interface.
/// Implementations must verify at connect time that the firmware advertises both the ownership-proof and
/// coinjoin-signing capabilities (as <see cref="PassportDevice.Open"/> does) and must never buffer or log
/// payloads beyond the exchange — requests carry derivation paths and PSBTs revealing wallet contents.
/// </summary>
public interface IPassportDevice : IDisposable
{
	byte ProtocolVersion { get; }
	uint Capabilities { get; }
	string FirmwareVersion { get; }

	/// <summary>Extended public key at the given derivation path (for wallet import).</summary>
	BitcoinExtPubKey GetXpub(KeyPath keyPath, Network network);

	/// <summary>
	/// Authorizes a coinjoin session on the device. The user reviews and approves the policy on the Passport
	/// screen; afterwards ownership proofs and signatures conforming to the policy are produced unattended.
	/// Returns the session id used by <see cref="GetOwnershipProof"/> and <see cref="SignCoinJoin"/>.
	/// </summary>
	uint AuthorizeCoinJoin(CoinjoinPolicy policy, int approvalTimeoutMs = 120000);

	/// <summary>
	/// Produces a SLIP-19 ownership proof for a coinjoin input under an authorized session. The returned bytes
	/// are the fully serialized proof, ready for <c>OwnershipProof.FromBytes</c> and the coordinator.
	/// </summary>
	byte[] GetOwnershipProof(uint sessionId, KeyPath keyPath, byte[] commitmentData);

	/// <summary>
	/// Signs the coinjoin PSBT under an authorized session. The device verifies the round conforms to the
	/// policy (inputs ours, outputs self-spend, fee within the cap) and signs only our inputs, returning the
	/// updated PSBT. Rejected without a screen prompt if the round is out of policy.
	/// </summary>
	byte[] SignCoinJoin(uint sessionId, byte[] psbt);

	/// <summary>Revokes a coinjoin session, disabling further unattended signing under it.</summary>
	void RevokeSession(uint sessionId);
}
