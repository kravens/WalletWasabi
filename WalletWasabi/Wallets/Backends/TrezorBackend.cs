using System.Collections.Concurrent;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Hwi;
using WalletWasabi.Hwi.Trezor;
using WalletWasabi.Models;
using WalletWasabi.WabiSabi.Client;

namespace WalletWasabi.Wallets.Backends;

/// <summary>
/// Trezor, reached through the Trezor Bridge. It is the only vendor that keeps coinjoin funds in a SLIP-25
/// account of their own, and the only one whose transport Wasabi may have to start itself - which is why it
/// is also the only backend that has anything to hand back to HWI.
/// </summary>
internal class TrezorBackend : IHardwareWalletBackend
{
	public TrezorBackend(Network network, Action<HardwareWalletTransport> onTransportStatusChanged)
	{
		_network = network;
		_bridge = new TrezorBridgeProcess();
		_bridge.StatusChanged += (_, status) => onTransportStatusChanged(status);
	}

	private readonly Network _network;
	private readonly TrezorBridgeProcess _bridge;

	/// <summary>The coinjoin key chain of each wallet, with the device session it holds. Opening the device again would end that session and its authorization, so the wallet's other device operations run on it.</summary>
	private readonly ConcurrentDictionary<KeyManager, TrezorKeyChain> _coinJoinKeyChains = new();

	public HardwareCoinJoinVendor Vendor => HardwareCoinJoinVendor.Trezor;

	public HardwareWalletTransport TransportStatus => _bridge.Status;

	public Task<bool> IsTransportAvailableAsync(CancellationToken cancellationToken) =>
		TrezorDevice.IsBridgeAvailableAsync(cancellationToken);

	/// <summary>Starts a bridge when none runs, for a wallet that reaches its device through one.</summary>
	public Task EnsureReadyAsync(CancellationToken cancellationToken) =>
		_bridge.EnsureRunningAsync(cancellationToken);

	/// <summary>Stops a bridge we started, so HWI can take the device; returns whether there was one to put back afterwards.</summary>
	public bool Release() => _bridge.StopIfOurs();

	/// <summary>The bridge stays while another wallet's coinjoin key chain still holds a device through it.</summary>
	public void ReleaseFor(KeyManager keyManager)
	{
		ForgetKeyChain(keyManager);
		if (_coinJoinKeyChains.IsEmpty)
		{
			_bridge.StopIfOurs();
		}
	}

	/// <summary>Drops a wallet's coinjoin key chain and releases the device session it holds.</summary>
	private void ForgetKeyChain(KeyManager keyManager)
	{
		if (_coinJoinKeyChains.TryRemove(keyManager, out var keyChain))
		{
			keyChain.Dispose();
		}
	}

	/// <summary>A Trezor wallet's icon is how a plain watch-only import is recognised as one; a coinjoin wallet is recognised by its account anyway.</summary>
	public bool SharesTransportWith(KeyManager keyManager) =>
		keyManager.Icon is { } icon && Enum.TryParse<WalletType>(icon, ignoreCase: true, out var walletType) && walletType is WalletType.Trezor;

	public async Task<KeyManager?> TryImportAsync(HDFingerprint? masterFingerprint, string walletFilePath, bool enableCoinjoin, IProgress<BitcoinAddress>? addressToConfirm, CancellationToken cancellationToken)
	{
		if (!enableCoinjoin && masterFingerprint is not null)
		{
			return null; // HWI reads a plain watch-only wallet by itself; only the coinjoin account needs the bridge.
		}

		using var device = await AcquireAsync(masterFingerprint, cancellationToken).ConfigureAwait(false);
		var fingerprint = masterFingerprint ?? await device.GetMasterFingerprintAsync(cancellationToken).ConfigureAwait(false);
		return await ReadAccountsAsync(device, fingerprint, walletFilePath, enableCoinjoin, addressToConfirm, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Adds the SLIP-25 coinjoin account, which the device confirms and then shows the first address of, as an import does, before the account is trusted.</summary>
	public async Task EnableCoinJoinAsync(KeyManager keyManager, IProgress<BitcoinAddress>? addressToConfirm, CancellationToken cancellationToken)
	{
		using var device = await AcquireAsync(keyManager.MasterFingerprint, cancellationToken).ConfigureAwait(false);
		var coinJoinAccountKeyPath = Slip25.GetCoinJoinAccountKeyPath(_network);
		var coinJoinExtPubKey = await device.GetAccountXpubAsync(coinJoinAccountKeyPath, _network, cancellationToken).ConfigureAwait(false);
		await ConfirmAccountOnDeviceAsync(device, coinJoinAccountKeyPath, coinJoinExtPubKey, addressToConfirm, cancellationToken).ConfigureAwait(false);

		// The vendor first: adopting the account writes the wallet file, and a restart must find both.
		keyManager.CoinJoinVendor = HardwareCoinJoinVendor.Trezor;
		keyManager.SetCoinJoinAccount(coinJoinAccountKeyPath, coinJoinExtPubKey);
	}

	/// <summary>Shows an address over the bridge, on the session the wallet's coinjoin key chain holds when there is one.</summary>
	public Task DisplayAddressAsync(KeyManager keyManager, KeyPath fullKeyPath, BitcoinAddress expectedAddress, CancellationToken cancellationToken) =>
		WithDeviceAsync(keyManager, device => ConfirmAddressOnDeviceAsync(device, fullKeyPath, expectedAddress, addressToConfirm: null, cancellationToken), cancellationToken);

	public async Task<IKeyChain> AuthorizeCoinJoinAsync(KeyManager keyManager, string coordinatorIdentifier, CancellationToken cancellationToken)
	{
		if (_coinJoinKeyChains.TryGetValue(keyManager, out var held) && !await held.Device.IsSessionAliveAsync(cancellationToken).ConfigureAwait(false))
		{
			// The bridge was restarted, dropped the device, or the wallet was stopped: every call on the old
			// session would fail forever, so let go of it and acquire the device anew.
			Logger.LogInfo("The Trezor bridge session of this wallet is gone, acquiring the device again.");
			ForgetKeyChain(keyManager);
		}

		bool acquiredHere = !_coinJoinKeyChains.TryGetValue(keyManager, out var keyChain);
		if (keyChain is null)
		{
			keyChain = new TrezorKeyChain(await AcquireAsync(keyManager.MasterFingerprint!.Value, cancellationToken).ConfigureAwait(false), keyManager);
			_coinJoinKeyChains[keyManager] = keyChain;
		}

		try
		{
			await keyChain.AuthorizeAsync(coordinatorIdentifier, cancellationToken).ConfigureAwait(false);
		}
		catch when (acquiredHere)
		{
			// The wallet never gets this key chain, so do not keep the session it holds.
			ForgetKeyChain(keyManager);
			throw;
		}

		return keyChain;
	}

	/// <summary>Acquires the device, starting a bridge for it first when none is running, so nothing has to be launched by hand.</summary>
	private async Task<TrezorDevice> AcquireAsync(HDFingerprint? masterFingerprint, CancellationToken cancellationToken)
	{
		await _bridge.EnsureRunningAsync(cancellationToken).ConfigureAwait(false);
		var sessionsInUse = _coinJoinKeyChains.Values.Select(keyChain => keyChain.Device.BridgeSession).ToHashSet();
		return await TrezorDevice.FindAsync(masterFingerprint, sessionsInUse, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Runs an operation on the session the wallet's coinjoin key chain holds, or on a session of its own when there is none.</summary>
	private async Task WithDeviceAsync(KeyManager keyManager, Func<TrezorDevice, Task> operation, CancellationToken cancellationToken)
	{
		if (_coinJoinKeyChains.TryGetValue(keyManager, out var held) && await held.Device.IsSessionAliveAsync(cancellationToken).ConfigureAwait(false))
		{
			await operation(held.Device).ConfigureAwait(false);
			return;
		}

		using var device = await AcquireAsync(keyManager.MasterFingerprint!.Value, cancellationToken).ConfigureAwait(false);
		await operation(device).ConfigureAwait(false);
	}

	/// <summary>
	/// Reads the accounts and has the device show the first address of each before the wallet is written: the
	/// bridge is an unauthenticated local process, so nothing it answers is trusted until the device itself has
	/// shown an address derived from it. A failed check leaves no wallet file behind.
	/// </summary>
	internal async Task<KeyManager> ReadAccountsAsync(TrezorDevice device, HDFingerprint fingerprint, string walletFilePath, bool enableCoinjoin, IProgress<BitcoinAddress>? addressToConfirm, CancellationToken cancellationToken)
	{
		var segwitAccountKeyPath = KeyManager.GetAccountKeyPath(_network, ScriptPubKeyType.Segwit);
		var segwitExtPubKey = await device.GetAccountXpubAsync(segwitAccountKeyPath, _network, cancellationToken).ConfigureAwait(false);
		await ConfirmAccountOnDeviceAsync(device, segwitAccountKeyPath, segwitExtPubKey, addressToConfirm, cancellationToken).ConfigureAwait(false);

		var keyManager = KeyManager.CreateNewHardwareWalletWatchOnly(fingerprint, segwitExtPubKey, null, null, null, _network, walletFilePath);
		keyManager.SetIcon(WalletType.Trezor);
		if (enableCoinjoin)
		{
			var coinJoinAccountKeyPath = Slip25.GetCoinJoinAccountKeyPath(_network);
			var coinJoinExtPubKey = await device.GetAccountXpubAsync(coinJoinAccountKeyPath, _network, cancellationToken).ConfigureAwait(false);
			await ConfirmAccountOnDeviceAsync(device, coinJoinAccountKeyPath, coinJoinExtPubKey, addressToConfirm, cancellationToken).ConfigureAwait(false);
			keyManager.CoinJoinVendor = HardwareCoinJoinVendor.Trezor;
			keyManager.SetCoinJoinAccount(coinJoinAccountKeyPath, coinJoinExtPubKey);
		}

		return keyManager;
	}

	/// <summary>Has the device show the first receive address of an account, derived from the xpub just read the way the wallet derives it.</summary>
	private Task ConfirmAccountOnDeviceAsync(TrezorDevice device, KeyPath accountKeyPath, ExtPubKey accountExtPubKey, IProgress<BitcoinAddress>? addressToConfirm, CancellationToken cancellationToken)
	{
		var fullKeyPath = accountKeyPath.Derive(0).Derive(0);
		var expectedAddress = accountExtPubKey.Derive(0).Derive(0).PubKey.GetAddress(fullKeyPath.GetScriptTypeFromKeyPath(), _network);
		return ConfirmAddressOnDeviceAsync(device, fullKeyPath, expectedAddress, addressToConfirm, cancellationToken);
	}

	/// <summary>Shows the address on the device and checks it against the wallet's. The user comparing the two screens is what proves the keys; this only catches a transport that showed nothing.</summary>
	private async Task ConfirmAddressOnDeviceAsync(TrezorDevice device, KeyPath fullKeyPath, BitcoinAddress expectedAddress, IProgress<BitcoinAddress>? addressToConfirm, CancellationToken cancellationToken)
	{
		addressToConfirm?.Report(expectedAddress);
		var shownAddress = await device.ShowAddressAsync(fullKeyPath, _network, cancellationToken).ConfigureAwait(false);
		if (shownAddress != expectedAddress.ToString())
		{
			throw new HardwareWalletException("The device shows a different address than the wallet. Do not use either of them.");
		}
	}

	public async Task<PSBT> SignOverBridgeAsync(KeyManager keyManager, PSBT psbt, SmartTransaction transaction, CancellationToken cancellationToken)
	{
		var globalTransaction = psbt.GetGlobalTransaction();
		bool spendsCoinJoinAccount = false;

		var inputs = psbt.Inputs
			.Select((input, index) =>
			{
				var keyPath = keyManager.TryGetKeyPath(input.WitnessUtxo?.ScriptPubKey ?? throw new InvalidOperationException("Cannot sign an input without its previous output."))
					?? throw new InvalidOperationException("Cannot sign an input that does not belong to this wallet.");

				bool isCoinJoinAccount = keyPath.IsSlip25KeyPath();
				spendsCoinJoinAccount |= isCoinJoinAccount;

				return new TrezorTxInput
				{
					AddressN = keyPath.Indexes,
					PrevHash = input.PrevOut.Hash.ToBytes(lendian: false),
					PrevIndex = input.PrevOut.N,
					Sequence = globalTransaction.Inputs[index].Sequence.Value,
					ScriptType = isCoinJoinAccount ? TrezorInputScriptType.SpendTaproot : TrezorInputScriptType.SpendWitness,
					Amount = (ulong)input.WitnessUtxo!.Value.Satoshi,
				};
			})
			.ToList();

		// An own output is streamed as a verifiable key path only when its account matches the unlock state of
		// the transaction: the device rejects a coinjoin account output path without the unlock, and a regular
		// one with it. A transfer between the two accounts is therefore shown as a plain address to confirm.
		var outputs = psbt.Outputs
			.Select(output =>
			{
				var keyPath = keyManager.TryGetKeyPath(output.ScriptPubKey);
				bool isCoinJoinAccount = keyPath?.IsSlip25KeyPath() is true;
				bool verifiableByPath = keyPath is not null && isCoinJoinAccount == spendsCoinJoinAccount;
				return new TrezorTxOutput
				{
					AddressN = verifiableByPath ? keyPath!.Indexes : [],
					Address = verifiableByPath ? "" : output.ScriptPubKey.GetDestinationAddress(_network)?.ToString()
						?? throw new InvalidOperationException("Cannot show an output that is not an address on the device."),
					Amount = (ulong)output.Value.Satoshi,
					ScriptType = !verifiableByPath
						? TrezorOutputScriptType.PayToAddress
						: isCoinJoinAccount
							? TrezorOutputScriptType.PayToTaproot
							: TrezorOutputScriptType.PayToWitness,
				};
			})
			.ToList();

		// The device verifies the spent amount of every non-taproot input against its previous transaction.
		var previousTransactions = transaction.WalletInputs
			.Select(coin => coin.Transaction.Transaction)
			.DistinctBy(tx => tx.GetHash())
			.ToDictionary(tx => tx.GetHash(), tx => tx);

		Dictionary<int, byte[]> signatures = [];
		await WithDeviceAsync(
			keyManager,
			async device => signatures = await device.SignTransactionAsync(
				inputs,
				outputs,
				globalTransaction.Version,
				globalTransaction.LockTime.Value,
				_network,
				unlockCoinJoinAccount: spendsCoinJoinAccount,
				previousTransactions,
				cancellationToken).ConfigureAwait(false),
			cancellationToken).ConfigureAwait(false);

		var signedPsbt = psbt.Clone();
		foreach (var (index, signature) in signatures)
		{
			signedPsbt.Inputs[index].FinalScriptWitness = inputs[index].ScriptType == TrezorInputScriptType.SpendTaproot
				? new WitScript(Op.GetPushOp(signature))
				: BuildSegwitWitness(keyManager, psbt.Inputs[index], signature);
		}

		HardwareWalletService.AssertSpendsWhatWasBuilt(psbt, signedPsbt);
		return signedPsbt;
	}

	/// <summary>A P2WPKH witness is the DER signature (with sighash byte) followed by the public key.</summary>
	private static WitScript BuildSegwitWitness(KeyManager keyManager, PSBTInput input, byte[] signature)
	{
		if (!keyManager.TryGetKeyForScriptPubKey(input.WitnessUtxo!.ScriptPubKey, out var hdPubKey))
		{
			throw new InvalidOperationException("Cannot find the public key of a signed input.");
		}

		// The device returns the DER signature without the sighash type byte.
		byte[] signatureWithSighash = [.. signature, (byte)SigHash.All];
		return new WitScript(Op.GetPushOp(signatureWithSighash), Op.GetPushOp(hdPubKey.PubKey.ToBytes()));
	}

	public void Dispose() => _bridge.Dispose();
}
