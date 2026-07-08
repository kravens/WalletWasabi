using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
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
	public ColdcardKeyChain(ColdcardDevice device, KeyManager keyManager)
	{
		if (!keyManager.IsHardwareWallet)
		{
			throw new ArgumentException("A Coldcard key chain requires a hardware wallet key manager.");
		}

		_device = device;
		_keyManager = keyManager;
	}

	private readonly ColdcardDevice _device;
	private readonly KeyManager _keyManager;
	private readonly object _signingLock = new();
	private (uint256 TxId, Dictionary<OutPoint, WitScript> Witnesses)? _signedTransactionCache;

	public ColdcardDevice Device => _device;

	public OwnershipProof GetOwnershipProof(IDestination destination, CoinJoinInputCommitmentData commitmentData)
	{
		var keyPath = _keyManager.TryGetKeyPath(destination.ScriptPubKey)
			?? throw new InvalidOperationException($"The key path for '{destination.ScriptPubKey}' was not found.");

		var proofBytes = _device.SignOwnershipProof(keyPath, commitmentData.ToBytes(), userConfirmation: true);
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
			txInput.WitScript = cache.Witnesses[coin.Outpoint];
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

		var ourOutpoints = transaction.Inputs.AsIndexedInputs()
			.Where(input => _keyManager.TryGetKeyPath(spentOutputs[(int)input.Index].ScriptPubKey) is not null)
			.Select(input => input.PrevOut)
			.ToHashSet();

		var signedBytes = _device.SignPsbt(psbt.ToBytes(), CancellationToken.None);
		var signedPsbt = PSBT.Load(signedBytes, network);
		signedPsbt.Finalize();

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
