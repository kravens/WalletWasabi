using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Crypto;
using WalletWasabi.Extensions;
using WalletWasabi.Hwi.Coldcard;
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
	public ColdcardKeyChain(ColdcardDevice device, KeyManager keyManager, ITransactionStore transactionStore, int maxRounds)
	{
		if (!keyManager.IsHardwareWallet)
		{
			throw new ArgumentException("A Coldcard key chain requires a hardware wallet key manager.");
		}

		_device = device;
		_keyManager = keyManager;
		_transactionStore = transactionStore;
		_maxRounds = maxRounds;
	}

	private readonly ColdcardDevice _device;
	private readonly KeyManager _keyManager;
	private readonly ITransactionStore _transactionStore;
	private readonly int _maxRounds;
	private int _roundsSigned;
	private readonly object _signingLock = new();
	private (uint256 TxId, Dictionary<OutPoint, WitScript> Witnesses)? _signedTransactionCache;

	public ColdcardDevice Device => _device;

	/// <summary>The device HSM policy has no round counter, so the user's round budget is enforced here:
	/// once it is used up, no new round can be entered until the user authorizes again.</summary>
	public bool RoundsExhausted => _roundsSigned >= _maxRounds;

	public OwnershipProof GetOwnershipProof(IDestination destination, CoinJoinInputCommitmentData commitmentData)
	{
		if (RoundsExhausted)
		{
			throw new ColdcardException($"The authorized {_maxRounds} coinjoin rounds are used up. Authorize the Coldcard again to continue.");
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

		// The device's HSM checks (fee limit, self-transfer floor) run on the input amounts the host
		// claims in witness_utxo. Give it the full previous transactions of our inputs so it verifies our
		// amounts instead of trusting them (closes the segwit v0 fee-overpayment shape for unattended
		// signing). Foreign prev txs are unknown here and not needed — we don't sign those inputs.
		psbt.AddPrevTxs(_transactionStore);

		var ourOutpoints = transaction.Inputs.AsIndexedInputs()
			.Where(input => _keyManager.TryGetKeyPath(spentOutputs[(int)input.Index].ScriptPubKey) is not null)
			.Select(input => input.PrevOut)
			.ToHashSet();

		// Bounded, so a wedged device can't hold the signing lock forever.
		using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
		var signedBytes = _device.SignPsbt(psbt.ToBytes(), timeout.Token);
		var signedPsbt = PSBT.Load(signedBytes, network);

		// Only our inputs can finalize; the foreign ones are witness-utxo-only and unsigned, so a full
		// Finalize() would throw on every multi-party round.
		signedPsbt.TryFinalize(out _);
		_roundsSigned++;

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
