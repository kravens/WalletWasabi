namespace WalletWasabi.WabiSabi.Client;

public class InternalDestinationProvider : IDestinationProvider
{
	/// <param name="canSign">Whether the wallet's signer can sign a script type; the outputs of this round are the inputs of the next, so a device gets none of a type it cannot sign.</param>
	public InternalDestinationProvider(KeyManager keyManager, Func<ScriptType, bool>? canSign = null)
	{
		_keyManager = keyManager;
		_canSign = canSign ?? (_ => true);
	}

	private readonly KeyManager _keyManager;
	private readonly Func<ScriptType, bool> _canSign;

	// Read live, so a coinjoin account added to a loaded wallet is used without restarting it.
	// A device authorization is bound to the SLIP-25 taproot account, so all outputs must stay in it.
	public ImmutableHashSet<ScriptType> SupportedScriptTypes
	{
		get
		{
			ImmutableHashSet<ScriptType> accounts = _keyManager.HasCoinJoinAccount
				? [ScriptType.Taproot]
				: _keyManager.TaprootExtPubKey is not null
					? [ScriptType.P2WPKH, ScriptType.Taproot]
					: [ScriptType.P2WPKH];
			return accounts.Where(_canSign).ToImmutableHashSet();
		}
	}

	public IEnumerable<IDestination> GetNextDestinations(int count, bool preferTaproot)
	{
		// A device can only sign coinjoin outputs of the SLIP-25 taproot account, so it never uses segwit destinations.
		bool taprootOnly = _keyManager.HasCoinJoinAccount;

		// Get all locked internal keys we have and assert we have enough.
		_keyManager.AssertLockedInternalKeysIndexedAndPersist(count, preferTaproot || taprootOnly);

		var allKeys = _keyManager.GetNextCoinJoinKeys().ToList();
		var taprootKeys = allKeys
			.Where(x => x.FullKeyPath.GetScriptTypeFromKeyPath() == ScriptPubKeyType.TaprootBIP86)
			.ToList();

		var segwitKeys = allKeys
			.Where(x => x.FullKeyPath.GetScriptTypeFromKeyPath() == ScriptPubKeyType.Segwit)
			.ToList();

		var destinations = taprootOnly || (preferTaproot && taprootKeys.Count >= count)
			? taprootKeys
			: segwitKeys;
		return destinations.Select(x => x.GetAddress(_keyManager.GetNetwork()));
	}

	public void TrySetScriptStates(KeyState state, IEnumerable<Script> scripts)
	{
		foreach (var hdPubKey in _keyManager.GetKeys(key => scripts.Any(key.ContainsScript)))
		{
			_keyManager.SetKeyState(state, hdPubKey);
		}

		_keyManager.ToFile();
	}
}
