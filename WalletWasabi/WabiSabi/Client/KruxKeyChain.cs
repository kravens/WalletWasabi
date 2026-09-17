using WalletWasabi.Hwi.Krux;
using WalletWasabi.WabiSabi.Models.MultipartyTransaction;

namespace WalletWasabi.WabiSabi.Client;

/// <summary>
/// Key chain backed by a Krux or SabiSigner acting as a remote signer for coinjoin rounds through the kruxd
/// bridge. The user pre-approves one signing session on the device (fingerprint, self-transfer floor, budget,
/// rounds), after which ownership proofs and signatures are produced without user interaction. The policy lives
/// on the device and is enforced there per PSBT; Wasabi-side settings only gate which rounds the client
/// attempts, so a compromised host cannot spend beyond the device policy.
/// </summary>
public class KruxKeyChain : IKeyChain, IDisposable
{
	/// <param name="roundsRemaining">
	/// What the device's approved session still allows, as the bridge reported it. The device enforces its own
	/// budget regardless; this keeps a coinjoin from starting on a session that has nothing left, which would
	/// fail only once it came time to sign.
	/// </param>
	/// <param name="scriptTypes">What the device signs in a round, as the bridge reported it; coins of other types stay out of rounds.</param>
	public KruxKeyChain(KruxClient client, KeyManager keyManager, int roundsRemaining = int.MaxValue, IReadOnlyCollection<ScriptType>? scriptTypes = null)
	{
		_client = client;
		_keyManager = keyManager;
		_roundsRemaining = roundsRemaining;
		_scriptTypes = scriptTypes ?? [ScriptType.P2WPKH, ScriptType.Taproot];
	}

	private readonly KruxClient _client;
	private readonly KeyManager _keyManager;
	private readonly IReadOnlyCollection<ScriptType> _scriptTypes;
	private readonly object _signingLock = new();
	private int _roundsRemaining;
	private (uint256 TxId, Dictionary<OutPoint, WitScript> Witnesses)? _signedTransactionCache;

	public KruxClient Client => _client;

	/// <summary>The session the user approved on the device runs out of rounds; it has to be approved again.</summary>
	public bool NeedsReauthorization => Volatile.Read(ref _roundsRemaining) <= 0;

	/// <summary>The device has to be asked over a UART or a HID session, and it verifies the whole round before it signs.</summary>
	public bool SigningTakesTime => true;

	/// <summary>The wallet's cap, enforced by Wasabi; the device budgets in satoshis and would refuse a round above its own limits anyway.</summary>
	public FeeRate? MaxMiningFeeRate { get; internal set; }

	public bool CanSign(ScriptType scriptType) => _scriptTypes.Contains(scriptType);

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
	/// The device validates and signs the whole coinjoin in a single PSBT round-trip, because every sign call
	/// spends one round of the session budget. The witnesses are cached, so the per-coin calls of the signing
	/// phase hit the device only once per round.
	/// </summary>
	public Transaction Sign(TransactionWithPrecomputedData unsignedCoinJoin, Coin coin)
	{
		lock (_signingLock)
		{
			var transaction = unsignedCoinJoin.Transaction;
			if (_signedTransactionCache is not { } cache || cache.TxId != transaction.GetHash())
			{
				var signedPsbt = _client
					.SignCoinJoinAsync(RemoteSignerPsbt.Build(unsignedCoinJoin, _keyManager), _keyManager.GetNetwork(), CancellationToken.None)
					.GetAwaiter()
					.GetResult();
				cache = (transaction.GetHash(), RemoteSignerPsbt.Witnesses(signedPsbt));
				_signedTransactionCache = cache;

				// One round of the approved session is spent per transaction signed, not per input.
				Interlocked.Decrement(ref _roundsRemaining);
			}

			transaction = transaction.Clone();
			var txInput = transaction.Inputs.AsIndexedInputs().FirstOrDefault(input => input.PrevOut == coin.Outpoint)
				?? throw new InvalidOperationException("Missing input.");
			txInput.WitScript = cache.Witnesses[coin.Outpoint];
			return transaction;
		}
	}

	public void Dispose()
	{
		_client.Dispose();
	}
}
