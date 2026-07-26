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
}
