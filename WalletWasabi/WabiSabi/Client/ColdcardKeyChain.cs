using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Crypto;
using WalletWasabi.Extensions;
using WalletWasabi.Hwi.Coldcard;
using WalletWasabi.Logging;
using WalletWasabi.WabiSabi.Models.MultipartyTransaction;

namespace WalletWasabi.WabiSabi.Client;

/// <summary>
/// Key chain backed by a Coldcard in HSM mode acting as a coinjoin remote signer. Ownership proofs use the
/// firmware's <c>slip19</c> command; the round is signed by handing the device a PSBT (our inputs carrying
/// their key derivations, foreign inputs their witness utxos only when taproot needs them) which it signs
/// unattended within its HSM policy. Uses the default segwit/taproot accounts — no SLIP-25 account like Trezor.
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
		_rounds = new(SignOnDevice);
		_maxRounds = maxRounds;
	}

	private readonly ColdcardDevice _device;
	private readonly KeyManager _keyManager;
	private readonly int _maxRounds;
	private int _roundsSigned;
	private readonly SignedRoundCache _rounds;

	public ColdcardDevice Device => _device;

	/// <summary>The device HSM policy has no round counter, so the user's round budget is enforced here:
	/// once it is used up, no new round can be entered until the user authorizes again.</summary>
	public bool NeedsAuthorization => Volatile.Read(ref _roundsSigned) >= _maxRounds;

	/// <summary>The policy's <c>max_fee_per_kvbyte</c>, so rounds the device would refuse are skipped before inputs register.</summary>
	public FeeRate MaxMiningFeeRate => new(_keyManager.CoinJoinDeviceMaxMiningFeeRate);

	/// <inheritdoc />
	public int? MinRoundInputs => _keyManager.ColdcardMinInputs > 0 ? _keyManager.ColdcardMinInputs : null;

	/// <inheritdoc />
	/// <remarks>Measured at 91-117s for a mainnet round, against a signing phase of about 90s.</remarks>
	public bool SigningTakesTime => true;

	/// <inheritdoc />
	/// <remarks>Taproot is optional: a Coldcard wallet is imported with a segwit account only, and has taproot coins
	/// only after the user adds the taproot account (<see cref="Wallets.HardwareWalletService.EnableTaprootAsync"/>).
	/// That requires Edge firmware, the only Coldcard line that signs taproot; <c>coinjoin-fw-2</c> is built on it.</remarks>
	public bool CanSign(ScriptType scriptType) => scriptType is ScriptType.P2WPKH or ScriptType.Taproot;

	public OwnershipProof GetOwnershipProof(IDestination destination, CoinJoinInputCommitmentData commitmentData)
	{
		if (NeedsAuthorization)
		{
			throw new ColdcardException(
				$"The authorized {_maxRounds} coinjoin rounds are used up. Authorize the Coldcard again to continue.");
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

		var proofBytes = _device.SignOwnershipProof(keyPath, scriptType, commitmentData.ToBytes());
		return OwnershipProof.FromBytes(proofBytes);
	}

	/// <summary>
	/// Signs all our inputs of the coinjoin in one device PSBT signing (cached per round), then serves the
	/// per-alice requests from the witness cache — one device round trip per coinjoin round.
	/// </summary>
	public Transaction Sign(TransactionWithPrecomputedData unsignedCoinJoin, Coin coin) => _rounds.Sign(unsignedCoinJoin, coin);

	private Dictionary<OutPoint, WitScript> SignOnDevice(TransactionWithPrecomputedData unsignedCoinJoin)
	{
		var network = _keyManager.GetNetwork();
		var (psbt, ourOutpoints) = CreatePsbt(unsignedCoinJoin, _keyManager);

		// Bounded so a wedged device cannot hold the signing lock forever; a mainnet round legitimately takes minutes.
		using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));

		// Timed and logged: the coordinator's signing phase, not this budget, is the real deadline.
		var psbtBytes = psbt.ToBytes();
		var started = DateTimeOffset.UtcNow;
		var signedBytes = _device.SignPsbt(psbtBytes, timeout.Token);
		var elapsed = (DateTimeOffset.UtcNow - started).TotalSeconds;
		var signedPsbt = PSBT.Load(signedBytes, network);

		// Counted before finalizing: TryFinalize consumes PartialSigs into FinalScriptWitness, so counting
		// after it reports zero signatures on every successful round - the opposite of what this is for.
		var ourSigned = signedPsbt.Inputs.Count(i => ourOutpoints.Contains(i.PrevOut) && (i.PartialSigs.Count > 0 || i.TaprootKeySignature is not null));

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

		// Written while signing, read by GetOwnershipProof while inputs register in parallel.
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

	/// <summary>The PSBT the device signs a round from, and which of its inputs are ours.</summary>
	internal static (PSBT Psbt, HashSet<OutPoint> OurOutpoints) CreatePsbt(TransactionWithPrecomputedData unsignedCoinJoin, KeyManager keyManager)
	{
		var transaction = unsignedCoinJoin.Transaction;
		var spentOutputs = ((TaprootReadyPrecomputedTransactionData)unsignedCoinJoin.PrecomputedTransactionData).SpentOutputs;
		var isOurs = spentOutputs.Select(output => keyManager.TryGetKeyPath(output.ScriptPubKey) is not null).ToArray();

		// Witness UTXOs and key paths for our inputs only. The device needs our amounts (BIP-143 signs them); the
		// foreign ones it never uses - the sighash commits to them through the unsigned transaction and the HSM
		// rules sum only our inputs - and leaving them out saves about a quarter of a mainnet round's bytes and
		// most of the device's per-input work, at ~2.7 ms per PSBT byte against a signing phase of about a minute.
		// A taproot signature commits to every input's amount and script (BIP-341), so a round with a taproot
		// input of ours has to carry them all.
		var signsTaproot = spentOutputs.Where((output, i) => isOurs[i] && output.ScriptPubKey.IsScriptType(ScriptType.Taproot)).Any();
		var psbt = PSBT.FromTransaction(transaction, keyManager.GetNetwork());
		for (int i = 0; i < psbt.Inputs.Count; i++)
		{
			if (isOurs[i] || signsTaproot)
			{
				psbt.Inputs[i].WitnessUtxo = spentOutputs[i];
			}
		}
		psbt.AddKeyPaths(keyManager);

		// No AddPrevTxs: uploading every parent transaction pushed mainnet signing past the signing phase, and a
		// host lying about one of our amounts only gets a signature invalid for the real UTXO - a wasted round,
		// not funds.
		var ourOutpoints = transaction.Inputs.Where((_, i) => isOurs[i]).Select(input => input.PrevOut).ToHashSet();
		return (psbt, ourOutpoints);
	}

	public void Dispose() => _device.Dispose();
}
