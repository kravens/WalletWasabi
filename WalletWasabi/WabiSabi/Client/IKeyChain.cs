namespace WalletWasabi.WabiSabi.Client;

public interface IKeyChain
{
	/// <summary>True when signing needs device I/O; such a signer gets the whole signing phase instead of a random slot in it.</summary>
	bool SigningTakesTime => false;

	/// <summary>The fee cap the device was authorized with, or null. Rounds above it are skipped before any input is registered, since the device would refuse to sign and the coordinator would ban the inputs.</summary>
	FeeRate? MaxMiningFeeRate => null;

	/// <summary>Fewest inputs a round must have before the signer will sign it, or null. The device enforces it either way; this only keeps the client out of rounds the device is certain to refuse.</summary>
	int? MinRoundInputs => null;

	/// <summary>True when the signer can produce a signature for this script type. A device handed an input it cannot sign does not necessarily say so, so the client must not register one.</summary>
	bool CanSign(ScriptType scriptType) => true;

	/// <summary>True when the signer needs the user to authorize again before it will sign more rounds: a batch authorization runs out, and a coinjoin must not start on a spent one.</summary>
	bool NeedsReauthorization => false;

	OwnershipProof GetOwnershipProof(IDestination destination, CoinJoinInputCommitmentData committedData);

	Transaction Sign(TransactionWithPrecomputedData unsignedCoinJoin, Coin coin);
}
