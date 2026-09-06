using NBitcoin;
using WalletWasabi.Crypto;
using WalletWasabi.WabiSabi.Models.MultipartyTransaction;

namespace WalletWasabi.WabiSabi.Client;

public interface IKeyChain
{
	/// <summary>
	/// Whether producing a signature takes a meaningful part of the signing phase, as it does when a device
	/// signs. The scheduler spreads signing requests over the phase to hide timing from the coordinator, which
	/// only holds when signing itself is instant; a signer that is not gets the phase instead of its leftovers.
	/// </summary>
	bool SigningTakesTime => false;

	/// <summary>
	/// The highest mining fee rate this signer will sign at, when it is a device that was authorized with a
	/// cap; null when no such cap exists. Rounds above it must be skipped before any input is registered,
	/// because the device refuses them at signing time and the coordinator then bans the registered inputs.
	/// </summary>
	FeeRate? MaxMiningFeeRate => null;

	/// <summary>
	/// Fewest inputs a round must have before the signer will sign it, or null if it has no such rule.
	/// A hardware signer can be given this limit so a compromised host cannot walk it into a round with
	/// nobody else in it. The device enforces it either way; this is here only so the client can tell
	/// whether a coordinator is capable of offering a round that large, instead of registering into rounds
	/// the device is certain to refuse.
	/// </summary>
	int? MinRoundInputs => null;

	/// <summary>
	/// True when the signer can produce a signature for this script type. A device handed an input it cannot
	/// sign does not necessarily say so - one firmware line skips a taproot input of ours silently and returns
	/// a PSBT with nothing in it - so the client must not register one.
	/// </summary>
	bool CanSign(ScriptType scriptType) => true;

	/// <summary>
	/// True when the signer needs the user to authorize again before it will sign more rounds, even though
	/// this key chain still exists. A device that authorizes a batch of rounds runs out of them; without this
	/// the coinjoin would start on a spent authorization and only fail once it came time to sign.
	/// </summary>
	bool NeedsReauthorization => false;

	OwnershipProof GetOwnershipProof(IDestination destination, CoinJoinInputCommitmentData committedData);

	Transaction Sign(TransactionWithPrecomputedData unsignedCoinJoin, Coin coin);
}
