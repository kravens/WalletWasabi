using WalletWasabi.Crypto;
using WalletWasabi.WabiSabi.Models.MultipartyTransaction;

namespace WalletWasabi.WabiSabi.Client;

public interface IKeyChain
{
	OwnershipProof GetOwnershipProof(IDestination destination, CoinJoinInputCommitmentData committedData);

	Transaction Sign(TransactionWithPrecomputedData unsignedCoinJoin, Coin coin);

	/// <summary>
	/// Fewest inputs a round must have before the signer will sign it, or null if it has no such rule.
	/// A hardware signer can be given this limit so a compromised host cannot walk it into a round with
	/// nobody else in it. The device enforces it either way; this is here only so the client can tell
	/// whether a coordinator is capable of offering a round that large, instead of registering into
	/// rounds the device is certain to refuse.
	/// </summary>
	int? MinRoundInputs => null;

	/// <summary>
	/// True when producing a signature takes a large share of the signing phase, as it does on a
	/// hardware signer. The client then asks for the signature as soon as the phase opens instead of
	/// scheduling it at a random point inside it. That random wait exists to hide our timing from the
	/// coordinator, and it assumes signing is free; a device that needs most of the phase to answer
	/// cannot pay for it, and a round missed is worse for privacy than a round joined predictably.
	/// </summary>
	bool SignsSlowly => false;

	/// <summary>
	/// True when the signer can produce a signature for this script type. A device handed an input it
	/// cannot sign does not necessarily say so — this firmware line skips a taproot input of ours
	/// silently and returns a PSBT with nothing in it — so the client must not register one.
	/// </summary>
	bool CanSign(ScriptType scriptType) => true;
}
