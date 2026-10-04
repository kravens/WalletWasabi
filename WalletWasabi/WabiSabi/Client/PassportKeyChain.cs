using System.IO;
using WalletWasabi.Hwi.Passport;
using WalletWasabi.WabiSabi.Models.MultipartyTransaction;

namespace WalletWasabi.WabiSabi.Client;

/// <summary>
/// Key chain backed by a Foundation Passport Prime acting as a coinjoin remote signer through
/// <see cref="IPassportDevice"/>. One on-device approval of a session policy (account, coordinator, fee budget,
/// rounds, lifetime) yields a random token; ownership proofs and signatures are then produced unattended under
/// it, the device checking every round against the policy, so a compromised host cannot spend beyond it. The
/// wallet's ordinary accounts are used, there is no SLIP-25 account like Trezor's.
/// </summary>
public class PassportKeyChain : IKeyChain, IDisposable
{
	/// <param name="roundsRemaining">The session's round budget; a coinjoin must not start on a spent one, which would fail only when it came time to sign.</param>
	/// <param name="expiresAt">When the device stops honouring the session on its own clock.</param>
	public PassportKeyChain(IPassportDevice device, byte[] sessionToken, KeyManager keyManager, int roundsRemaining, DateTimeOffset expiresAt)
	{
		_device = device;
		_sessionToken = sessionToken;
		_keyManager = keyManager;
		_roundsRemaining = roundsRemaining;
		_expiresAt = expiresAt;
		_rounds = new(SignOnDevice);
	}

	private readonly IPassportDevice _device;
	private readonly byte[] _sessionToken;
	private readonly KeyManager _keyManager;
	private readonly DateTimeOffset _expiresAt;
	private readonly SignedRoundCache _rounds;
	private int _roundsRemaining;

	/// <summary>
	/// The approved session runs out of rounds or of time, or the device stopped answering; it has to be approved
	/// again on the device. The manager asks this before every round, so an unplugged Passport stops the coinjoin
	/// there instead of failing round after round with inputs it cannot prove.
	/// </summary>
	public bool NeedsAuthorization => Volatile.Read(ref _roundsRemaining) <= 0 || DateTimeOffset.UtcNow >= _expiresAt || !_device.IsAlive();

	/// <summary>The device verifies the whole round over USB before it signs.</summary>
	public bool SigningTakesTime => true;

	/// <summary>The wallet's cap, enforced by Wasabi, so a change applies at once; the device budgets in satoshis and would refuse a round above its own limits anyway.</summary>
	public FeeRate MaxMiningFeeRate => new(_keyManager.CoinJoinDeviceMaxMiningFeeRate);

	public bool CanSign(ScriptType scriptType) =>
		scriptType == ScriptType.P2WPKH || (scriptType == ScriptType.Taproot && _device.SupportsTaproot);

	public OwnershipProof GetOwnershipProof(IDestination destination, CoinJoinInputCommitmentData commitmentData)
	{
		var keyPath = _keyManager.TryGetKeyPath(destination.ScriptPubKey)
			?? throw new InvalidOperationException($"The key path for '{destination.ScriptPubKey}' was not found.");

		return OwnershipProof.FromBytes(_device.GetOwnershipProof(_sessionToken, keyPath, commitmentData.ToBytes()));
	}

	/// <summary>
	/// The device validates and signs the whole coinjoin in a single PSBT round trip, because every sign call
	/// spends one round of the session budget. The witnesses are cached, so the per-coin calls of the signing
	/// phase hit the device only once per round.
	/// </summary>
	public Transaction Sign(TransactionWithPrecomputedData unsignedCoinJoin, Coin coin) => _rounds.Sign(unsignedCoinJoin, coin);

	private Dictionary<OutPoint, WitScript> SignOnDevice(TransactionWithPrecomputedData unsignedCoinJoin)
	{
		var signedBytes = _device.SignCoinJoin(_sessionToken, RemoteSignerPsbt.Build(unsignedCoinJoin, _keyManager).ToBytes());

		// One round of the approved session is spent per transaction signed, not per input.
		Interlocked.Decrement(ref _roundsRemaining);
		return RemoteSignerPsbt.Witnesses(PSBT.Load(signedBytes, _keyManager.GetNetwork()));
	}

	/// <summary>Revokes the session, so the device zeroizes its keys, then closes the device. A session the device has already dropped is nothing to revoke.</summary>
	public void Dispose()
	{
		try
		{
			_device.RevokeSession(_sessionToken);
		}
		catch (Exception e) when (e is HardwareWalletException or IOException)
		{
			Logger.LogDebug($"Passport session not revoked: {e.Message}");
		}
		finally
		{
			Array.Clear(_sessionToken);
			_device.Dispose();
		}
	}
}
