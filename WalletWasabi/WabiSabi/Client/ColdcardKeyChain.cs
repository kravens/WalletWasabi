using System.IO;
using System.Threading;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Crypto;
using WalletWasabi.Hwi.Coldcard;
using WalletWasabi.Logging;
using WalletWasabi.WabiSabi.Models.MultipartyTransaction;
using WalletWasabi.Wallets;

namespace WalletWasabi.WabiSabi.Client;

/// <summary>
/// Key chain backed by a Coldcard in HSM mode acting as a coinjoin remote signer. Ownership proofs use the
/// firmware's <c>slp9</c> command; the round is signed from one <see cref="RemoteSignerPsbt"/>, which the device
/// signs unattended within its HSM policy. Uses the default segwit/taproot accounts — no SLIP-25 account like Trezor.
/// </summary>
public class ColdcardKeyChain : IKeyChain, IDisposable
{
	/// <summary>Takes the wallet's limits as they are now, which is what the policy the device just approved was composed from.</summary>
	public ColdcardKeyChain(ColdcardDevice device, KeyManager keyManager)
	{
		if (!keyManager.IsHardwareWallet)
		{
			throw new ArgumentException("A Coldcard key chain requires a hardware wallet key manager.");
		}

		_device = device;
		_keyManager = keyManager;
		_rounds = new(SignOnDevice);
		_maxRounds = keyManager.CoinJoinDeviceMaxRounds;
		_approvedMaxMiningFeeRate = new FeeRate(keyManager.CoinJoinDeviceMaxMiningFeeRate);
		_approvedMinInputs = keyManager.ColdcardMinInputs;
	}

	private readonly ColdcardDevice _device;
	private readonly KeyManager _keyManager;
	private readonly int _maxRounds;
	private readonly FeeRate _approvedMaxMiningFeeRate;
	private readonly int _approvedMinInputs;
	private int _roundsSigned;
	private readonly SignedRoundCache _rounds;

	/// <summary>The device HSM policy has no round counter, so the user's round budget is enforced here:
	/// once it is used up, no new round can be entered until the user authorizes again.</summary>
	private bool BudgetSpent => Volatile.Read(ref _roundsSigned) >= _maxRounds;

	/// <summary>
	/// The budget, and whether the device still runs its policy. The manager asks this before every round, so
	/// an unplugged Coldcard stops the coinjoin there instead of failing round after round with inputs it
	/// cannot prove. A round trip, not a USB enumeration: a device with no readable serial would enumerate as
	/// absent. The HSM status rather than a version call, because a Coldcard draws its power from USB: plugged
	/// back in it has rebooted, the reconnected session answers, and the policy is only running again if the
	/// user approved it at login; otherwise the device has to be asked again.
	/// </summary>
	public bool NeedsAuthorization => BudgetSpent || !IsPolicyRunning();

	/// <summary>
	/// The stricter of the policy's <c>max_fee_per_kvbyte</c> and the current setting, so rounds the device would
	/// refuse are skipped before inputs register. The device enforces the cap it approved until it reboots, so a
	/// cap raised afterwards must not move what the client picks rounds by - the device would refuse after the
	/// inputs registered, which bans them - while one lowered afterwards may.
	/// </summary>
	public FeeRate MaxMiningFeeRate => FeeRate.Min(_approvedMaxMiningFeeRate, new FeeRate(_keyManager.CoinJoinDeviceMaxMiningFeeRate));

	/// <inheritdoc />
	/// <remarks>The higher of the approved floor and the current setting, for the same reason as the fee cap.</remarks>
	public int? MinRoundInputs => Math.Max(_approvedMinInputs, _keyManager.ColdcardMinInputs) is > 0 and var floor ? floor : null;

	/// <inheritdoc />
	/// <remarks>Measured at 91-117s for a mainnet round, against a signing phase of about 90s.</remarks>
	public bool SigningTakesTime => true;

	public OwnershipProof GetOwnershipProof(IDestination destination, CoinJoinInputCommitmentData commitmentData)
	{
		if (BudgetSpent)
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

		try
		{
			return OwnershipProof.FromBytes(_device.SignOwnershipProof(keyPath, scriptType, commitmentData.ToBytes()));
		}
		catch (IOException e)
		{
			throw Unreachable(e);
		}
	}

	/// <summary>
	/// Signs all our inputs of the coinjoin in one device PSBT signing (cached per round), then serves the
	/// per-alice requests from the witness cache — one device round trip per coinjoin round.
	/// </summary>
	public Transaction Sign(TransactionWithPrecomputedData unsignedCoinJoin, Coin coin) => _rounds.Sign(unsignedCoinJoin, coin);

	/// <remarks>
	/// The PSBT carries every input's witness UTXO, foreign ones included: <c>coinjoin-fw-2</c> fails a round without
	/// them late in signing, segwit-only rounds too. That costs time, at about 4 ms per PSBT byte on a Mk4, so a
	/// large round can outlast a coordinator's signing phase.
	/// </remarks>
	private Dictionary<OutPoint, WitScript> SignOnDevice(TransactionWithPrecomputedData unsignedCoinJoin)
	{
		// Bounded so a wedged device cannot hold the signing lock forever; a mainnet round legitimately takes minutes.
		using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
		byte[] signedBytes;
		try
		{
			signedBytes = _device.SignPsbt(RemoteSignerPsbt.Build(unsignedCoinJoin, _keyManager).ToBytes(), timeout.Token);
		}
		catch (IOException e)
		{
			throw Unreachable(e);
		}

		// Written while signing, read by GetOwnershipProof while inputs register in parallel.
		Interlocked.Increment(ref _roundsSigned);
		return RemoteSignerPsbt.Witnesses(PSBT.Load(signedBytes, _keyManager.GetNetwork()));
	}

	private bool IsPolicyRunning()
	{
		try
		{
			if (_device.GetHsmStatus().Active)
			{
				return true;
			}

			Logger.LogInfo("The Coldcard is not in HSM mode; the coinjoin policy has to be approved on it again.");
			return false;
		}
		catch (Exception e)
		{
			Logger.LogDebug($"The Coldcard did not answer: {e.Message}");
			return false;
		}
	}

	/// <summary>An unplugged device fails at the HID layer; name it as the device going away, which is what the manager stops on.</summary>
	private static HardwareWalletNotFoundException Unreachable(IOException e) =>
		new($"The Coldcard stopped answering; check that it is connected. {e.Message}", e);

	public void Dispose() => _device.Dispose();
}
