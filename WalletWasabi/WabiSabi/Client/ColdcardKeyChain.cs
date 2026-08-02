using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Crypto;
using WalletWasabi.Extensions;
using WalletWasabi.Hwi.Coldcard;
using WalletWasabi.Logging;
using WalletWasabi.Hwi.Trezor;
using WalletWasabi.WabiSabi.Models.MultipartyTransaction;

namespace WalletWasabi.WabiSabi.Client;

/// <summary>
/// Key chain backed by a Coldcard in HSM mode acting as a coinjoin remote signer. Ownership proofs use the
/// firmware's <c>slip19</c> command; the round is signed by handing the device a PSBT (our inputs carrying
/// their key derivations, foreign inputs as witness-utxo only) which it signs unattended within its HSM
/// policy. Uses the default segwit/taproot accounts — no SLIP-25 account like Trezor.
/// </summary>
public class ColdcardKeyChain : IKeyChain, IDisposable
{
	public ColdcardKeyChain(ColdcardDevice device, KeyManager keyManager, int maxRounds)
	{
		if (!keyManager.IsHardwareWallet)
		{
			throw new ArgumentException("A Coldcard key chain requires a hardware wallet key manager.");
		}

		_device = device;
		_keyManager = keyManager;
		_maxRounds = maxRounds;
	}

	private readonly ColdcardDevice _device;
	private readonly KeyManager _keyManager;
	private readonly int _maxRounds;
	private int _roundsSigned;
	private readonly object _signingLock = new();
	private (uint256 TxId, Dictionary<OutPoint, WitScript> Witnesses)? _signedTransactionCache;

	public ColdcardDevice Device => _device;

	/// <summary>The device HSM policy has no round counter, so the user's round budget is enforced here:
	/// once it is used up, no new round can be entered until the user authorizes again.</summary>
	public bool RoundsExhausted => Volatile.Read(ref _roundsSigned) >= _maxRounds;

	/// <inheritdoc />
	public int? MinRoundInputs => _keyManager.ColdcardMinInputs > 0 ? _keyManager.ColdcardMinInputs : null;

	/// <inheritdoc />
	/// <remarks>Measured at 91-117s for a mainnet round, against a signing phase of about 90s.</remarks>
	public bool SignsSlowly => true;

	/// <inheritdoc />
	/// <remarks>This firmware line has no taproot support: <c>psbt.py</c> never parses
	/// PSBT_IN_TAP_BIP32_DERIVATION, so a taproot input of ours arrives with no derivation the device
	/// recognises and is skipped without any error. Registering one would look like the device simply
	/// failing to sign.</remarks>
	public bool CanSign(ScriptType scriptType) => scriptType == ScriptType.P2WPKH;

	public OwnershipProof GetOwnershipProof(IDestination destination, CoinJoinInputCommitmentData commitmentData)
	{
		if (RoundsExhausted)
		{
			throw new ColdcardException(
				$"The authorized {_maxRounds} coinjoin rounds are used up. Authorize the Coldcard again to continue.",
				"Round budget used up");
		}

		var keyPath = _keyManager.TryGetKeyPath(destination.ScriptPubKey)
			?? throw new InvalidOperationException($"The key path for '{destination.ScriptPubKey}' was not found.");

		// Tell the device which script we are proving, rather than letting it infer one from the path.
		var scriptPubKey = destination.ScriptPubKey;
		var scriptType = scriptPubKey.IsScriptType(ScriptType.Taproot)
			? ScriptPubKeyType.TaprootBIP86
			: scriptPubKey.IsScriptType(ScriptType.P2WPKH)
				? ScriptPubKeyType.Segwit
				: throw new NotSupportedException($"A Coldcard cannot prove ownership of '{scriptPubKey}'.");

		var proofBytes = _device.SignOwnershipProof(keyPath, scriptType, commitmentData.ToBytes(), userConfirmation: true);
		return OwnershipProof.FromBytes(proofBytes);
	}

	/// <summary>
	/// Signs all our inputs of the coinjoin in one device PSBT signing (cached per round), then serves the
	/// per-alice requests from the witness cache — one device round trip per coinjoin round.
	/// </summary>
	public Transaction Sign(TransactionWithPrecomputedData unsignedCoinJoin, Coin coin)
	{
		lock (_signingLock)
		{
			var transaction = unsignedCoinJoin.Transaction;
			if (_signedTransactionCache is not { } cache || cache.TxId != transaction.GetHash())
			{
				cache = (transaction.GetHash(), SignOnDevice(unsignedCoinJoin));
				_signedTransactionCache = cache;
			}

			transaction = transaction.Clone();
			var txInput = transaction.Inputs.AsIndexedInputs().FirstOrDefault(input => input.PrevOut == coin.Outpoint)
				?? throw new InvalidOperationException("Missing input.");
			if (!cache.Witnesses.TryGetValue(coin.Outpoint, out var witness))
			{
				throw new InvalidOperationException($"The device did not sign the input '{coin.Outpoint}'.");
			}
			txInput.WitScript = witness;
			return transaction;
		}
	}

	private Dictionary<OutPoint, WitScript> SignOnDevice(TransactionWithPrecomputedData unsignedCoinJoin)
	{
		var network = _keyManager.GetNetwork();
		var transaction = unsignedCoinJoin.Transaction;
		var spentOutputs = ((TaprootReadyPrecomputedTransactionData)unsignedCoinJoin.PrecomputedTransactionData).SpentOutputs;

		// Build the PSBT: witness UTXOs for every input (so the device sees the amounts), plus key paths for
		// the inputs that are ours (so it signs them; foreign inputs are left for their owners to sign).
		var psbt = PSBT.FromTransaction(transaction, network);
		for (int i = 0; i < psbt.Inputs.Count; i++)
		{
			psbt.Inputs[i].WitnessUtxo = spentOutputs[i];
		}
		psbt.AddKeyPaths(_keyManager);

		// Deliberately no AddPrevTxs. It was here so the device could verify our claimed input amounts
		// rather than trust witness_utxo, but the protection is redundant for this flow and the cost is
		// not: each parent transaction is uploaded, parsed and hashed on the device, and on mainnet that
		// pushed signing past the coordinator's signing phase, losing rounds the device would otherwise
		// have signed.
		//
		// Redundant because BIP-143 commits each signature to the amount of the input it signs. A host
		// that lies about one of our amounts gets a signature that is invalid for the real UTXO, so the
		// transaction cannot confirm. There is no multi-session gap to exploit either: every one of our
		// inputs is signed in this single pass, which is the shape the segwit fee attack needed. P2TR is
		// stronger still, committing to all input amounts at once.
		//
		// What a lie can still do is flatter the policy evaluation - own_in_value comes from the same
		// claimed amounts - so the device might approve a transaction it should have refused. That
		// transaction is unusable, because the signature over the lied-about input is invalid. The cost
		// of that is a wasted round and one of max_txn, not funds.
		//
		// Foreign input amounts never enter the policy: it sums only inputs with num_our_keys.

		var ourOutpoints = transaction.Inputs.AsIndexedInputs()
			.Where(input => _keyManager.TryGetKeyPath(spentOutputs[(int)input.Index].ScriptPubKey) is not null)
			.Select(input => input.PrevOut)
			.ToHashSet();

		// Bounded, so a wedged device can't hold the signing lock forever. Two minutes was enough for
		// regtest and not for mainnet: a real round's PSBT is far larger, and the parent transactions
		// attached for amount verification larger still, so the device legitimately needs longer than
		// that. Observed signing successfully while Wasabi had already given up on it.
		using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));

		// Timed and logged: the coordinator's signing phase is the real deadline, not this budget, and it
		// is not ours to set. Knowing how long the device actually takes against the PSBT size is what
		// tells us whether unattended signing is viable at live round sizes at all.
		var psbtBytes = psbt.ToBytes();
		var started = DateTimeOffset.UtcNow;
		var signedBytes = _device.SignPsbt(psbtBytes, timeout.Token);
		var elapsed = (DateTimeOffset.UtcNow - started).TotalSeconds;
		var signedPsbt = PSBT.Load(signedBytes, network);

		// Counted before finalizing: TryFinalize consumes PartialSigs into FinalScriptWitness, so counting
		// after it reports zero signatures on every successful round - the opposite of what this is for.
		var ourSigned = signedPsbt.Inputs.Count(i => ourOutpoints.Contains(i.PrevOut) && i.PartialSigs.Count > 0);

		// Only our inputs can finalize; the foreign ones are witness-utxo-only and unsigned, so a full
		// Finalize() would throw on every multi-party round.
		signedPsbt.TryFinalize(out _);
		var ourFinal = signedPsbt.Inputs.Count(i => ourOutpoints.Contains(i.PrevOut) && i.FinalScriptWitness is not null);

		Logger.LogInfo(
			$"Coldcard returned {ourSigned} signature(s) and {ourFinal} finalized witness(es) for the "
			+ $"{ourOutpoints.Count} input(s) we asked about, from a {psbtBytes.Length:N0}-byte PSBT in {elapsed:F1}s.");

		if (ourFinal < ourOutpoints.Count)
		{
			// A device that signs nothing, and one that signs but will not finalize, look identical here:
			// both return a PSBT. Say which it was, because the round is about to fail either way and the
			// two have completely different causes.
			Logger.LogWarning(
				$"Coldcard returned no usable witness for {ourOutpoints.Count - ourFinal} of our input(s). "
				+ $"It reported {ourSigned} signature(s), so it {(ourSigned > 0 ? "signed but finalization failed" : "did not sign at all")}.");
		}

		// Written under _signingLock but read by GetOwnershipProof, which runs unsynchronised while
		// inputs register in parallel.
		Interlocked.Increment(ref _roundsSigned);

		var witnesses = new Dictionary<OutPoint, WitScript>();
		foreach (var input in signedPsbt.Inputs)
		{
			if (ourOutpoints.Contains(input.PrevOut) && input.FinalScriptWitness is { } witness)
			{
				witnesses[input.PrevOut] = witness;
			}
		}

		return witnesses;
	}

	public void Dispose() => _device.Dispose();
}
