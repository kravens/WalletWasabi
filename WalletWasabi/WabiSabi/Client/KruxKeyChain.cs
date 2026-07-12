using NBitcoin;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Crypto;
using WalletWasabi.Hwi.Krux;
using WalletWasabi.WabiSabi.Models.MultipartyTransaction;

namespace WalletWasabi.WabiSabi.Client;

/// <summary>
/// Key chain backed by a Krux device acting as a remote signer for coinjoin rounds through
/// the kruxd bridge. The user pre-approves one signing session on the device ("CoinJoin USB"
/// screen: fingerprint, self-transfer floor, fee cap, round budget), after which ownership
/// proofs and signatures are produced without user interaction. The policy itself lives on
/// the device and is enforced there per PSBT; Wasabi-side settings only gate which rounds
/// the client attempts, so a compromised host cannot spend beyond the device policy.
/// </summary>
public class KruxKeyChain : IKeyChain, IDisposable
{
	public KruxKeyChain(KruxClient client, KeyManager keyManager)
	{
		if (!keyManager.IsHardwareWallet)
		{
			throw new ArgumentException("A Krux key chain requires a hardware wallet key manager.");
		}

		_client = client;
		_keyManager = keyManager;
	}

	private readonly KruxClient _client;
	private readonly KeyManager _keyManager;
	private readonly object _signingLock = new();
	private (uint256 TxId, Dictionary<OutPoint, WitScript> Witnesses)? _signedTransactionCache;

	public KruxClient Client => _client;

	public OwnershipProof GetOwnershipProof(IDestination destination, CoinJoinInputCommitmentData commitmentData)
	{
		if (!_keyManager.TryGetKeyForScriptPubKey(destination.ScriptPubKey, out HdPubKey? hdPubKey))
		{
			throw new InvalidOperationException($"The key for '{destination.ScriptPubKey}' was not found.");
		}

		var scriptType = destination.ScriptPubKey.IsScriptType(ScriptType.Taproot)
			? ScriptPubKeyType.TaprootBIP86
			: ScriptPubKeyType.Segwit;

		byte[] proof = _client
			.GetOwnershipProofAsync(hdPubKey.FullKeyPath, scriptType, commitmentData.ToBytes(), CancellationToken.None)
			.GetAwaiter()
			.GetResult();

		return OwnershipProof.FromBytes(proof);
	}

	/// <summary>
	/// The device validates and signs the whole coinjoin in a single PSBT round-trip, because every
	/// sign call spends one round of the session budget. The witnesses are cached, so the per-coin
	/// calls of the signing phase hit the device only once per round.
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
		var psbt = BuildPsbt(unsignedCoinJoin, network);

		var signedPsbt = _client
			.SignCoinJoinAsync(psbt, network, CancellationToken.None)
			.GetAwaiter()
			.GetResult();

		var witnesses = new Dictionary<OutPoint, WitScript>();
		foreach (var input in signedPsbt.Inputs)
		{
			if (input.TaprootKeySignature is { } taprootSignature)
			{
				witnesses[input.PrevOut] = new WitScript(Op.GetPushOp(taprootSignature.ToBytes()));
			}
			else if (input.PartialSigs.FirstOrDefault() is { Key: not null } partialSig)
			{
				witnesses[input.PrevOut] = new WitScript(
					Op.GetPushOp(partialSig.Value.ToBytes()),
					Op.GetPushOp(partialSig.Key.ToBytes()));
			}
		}

		if (witnesses.Count == 0)
		{
			throw new InvalidOperationException("The device returned no signatures.");
		}
		return witnesses;
	}

	/// <summary>
	/// Frames the unsigned coinjoin as a PSBT: every input gets its witness UTXO, ours also get
	/// their BIP-32 derivation so the device can recognize them; our outputs get derivations too,
	/// which is how the device tallies the self-transfer its policy demands.
	/// </summary>
	private PSBT BuildPsbt(TransactionWithPrecomputedData unsignedCoinJoin, Network network)
	{
		var transaction = unsignedCoinJoin.Transaction;
		var spentOutputs = ((TaprootReadyPrecomputedTransactionData)unsignedCoinJoin.PrecomputedTransactionData).SpentOutputs;
		var psbt = PSBT.FromTransaction(transaction, network);
		var masterFingerprint = _keyManager.MasterFingerprint
			?? throw new InvalidOperationException("The wallet has no master fingerprint.");

		foreach (var (input, index) in psbt.Inputs.Select((input, index) => (input, index)))
		{
			var spentOutput = spentOutputs[index];
			input.WitnessUtxo = spentOutput;
			AddDerivation(input, spentOutput.ScriptPubKey, masterFingerprint);
		}

		foreach (var output in psbt.Outputs)
		{
			AddDerivation(output, output.ScriptPubKey, masterFingerprint);
		}

		return psbt;
	}

	private void AddDerivation(PSBTCoin psbtCoin, Script scriptPubKey, HDFingerprint masterFingerprint)
	{
		if (!_keyManager.TryGetKeyForScriptPubKey(scriptPubKey, out HdPubKey? hdPubKey))
		{
			return; // foreign script, the device treats it as such
		}

		var rootedKeyPath = new RootedKeyPath(masterFingerprint, hdPubKey.FullKeyPath);
		if (scriptPubKey.IsScriptType(ScriptType.Taproot))
		{
			// BIP-371 keys taproot derivations by the x-only internal key; NBitcoin's map just wants 32 bytes.
			psbtCoin.HDTaprootKeyPaths.Add(new TaprootPubKey(hdPubKey.PubKey.TaprootInternalKey.ToBytes()), new TaprootKeyPath(rootedKeyPath));
		}
		else
		{
			psbtCoin.HDKeyPaths.Add(hdPubKey.PubKey, rootedKeyPath);
		}
	}

	public void Dispose()
	{
		_client.Dispose();
	}
}
