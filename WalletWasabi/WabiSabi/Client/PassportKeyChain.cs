using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Crypto;
using WalletWasabi.Extensions;
using WalletWasabi.Hwi.Passport;
using WalletWasabi.Hwi.Trezor;
using WalletWasabi.WabiSabi.Models.MultipartyTransaction;

namespace WalletWasabi.WabiSabi.Client;

/// <summary>
/// Key chain backed by a Foundation Passport Prime acting as a coinjoin remote signer over its wallet-rpc USB
/// protocol. A one-time on-device session authorization (see <see cref="Wallets.Wallet.AuthorizeHardwareCoinJoinAsync"/>)
/// fixes the account, coordinator, per-round fee cap and self-spend rule; afterwards ownership proofs use the
/// firmware's SLIP-19 command and the round is signed by handing the device a PSBT it verifies against the
/// authorized policy and signs unattended. Uses the default segwit account — no SLIP-25 account like Trezor.
/// </summary>
public class PassportKeyChain : IKeyChain, IDisposable
{
	public PassportKeyChain(PassportDevice device, uint sessionId, KeyManager keyManager)
	{
		if (!keyManager.IsHardwareWallet)
		{
			throw new ArgumentException("A Passport key chain requires a hardware wallet key manager.");
		}

		_device = device;
		_sessionId = sessionId;
		_keyManager = keyManager;
	}

	private readonly PassportDevice _device;
	private readonly uint _sessionId;
	private readonly KeyManager _keyManager;
	private readonly object _signingLock = new();
	private (uint256 TxId, Dictionary<OutPoint, WitScript> Witnesses)? _signedTransactionCache;

	public PassportDevice Device => _device;

	public OwnershipProof GetOwnershipProof(IDestination destination, CoinJoinInputCommitmentData commitmentData)
	{
		var keyPath = _keyManager.TryGetKeyPath(destination.ScriptPubKey)
			?? throw new InvalidOperationException($"The key path for '{destination.ScriptPubKey}' was not found.");

		var proofBytes = _device.GetOwnershipProof(_sessionId, keyPath, commitmentData.ToBytes());
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

		// Build the PSBT: witness UTXOs for every input (so the device sees the amounts and can enforce its
		// fee cap), plus BIP-32 derivations for the inputs and outputs that are ours (so it signs our inputs
		// and credits our outputs as self-spend). Foreign inputs/outputs carry no derivations.
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

		var signedBytes = _device.SignCoinJoin(_sessionId, psbt.ToBytes());
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
