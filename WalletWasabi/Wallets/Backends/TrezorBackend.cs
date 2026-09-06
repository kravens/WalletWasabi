using NBitcoin;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Hwi;
using WalletWasabi.Hwi.Trezor;
using WalletWasabi.Logging;
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

	public HardwareCoinJoinVendor Vendor => HardwareCoinJoinVendor.Trezor;

	public HardwareWalletTransport TransportStatus => _bridge.Status;

	public Task<bool> IsTransportAvailableAsync(CancellationToken cancellationToken) =>
		TrezorDevice.IsBridgeAvailableAsync(cancellationToken);

	public Task EnsureReadyAsync(CancellationToken cancellationToken) =>
		_bridge.EnsureRunningAsync(cancellationToken);

	public bool Release() => _bridge.StopIfOurs();

	/// <summary>
	/// A Trezor wallet's icon is how a plain watch-only import is recognised as one; a coinjoin wallet is
	/// recognised by its account anyway.
	/// </summary>
	public bool SharesTransportWith(KeyManager keyManager) =>
		keyManager.Icon is { } icon && Enum.TryParse<WalletType>(icon, ignoreCase: true, out var walletType) && walletType is WalletType.Trezor;

	public async Task<KeyManager?> TryImportAsync(HDFingerprint? masterFingerprint, string walletFilePath, bool enableCoinjoin, CancellationToken cancellationToken)
	{
		using var device = await AcquireAsync(masterFingerprint, cancellationToken).ConfigureAwait(false);
		var fingerprint = masterFingerprint ?? await device.GetMasterFingerprintAsync(cancellationToken).ConfigureAwait(false);
		return await ReadAccountsAsync(device, fingerprint, walletFilePath, enableCoinjoin, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Adds the SLIP-25 coinjoin account, which the device shows for confirmation.</summary>
	public async Task EnableCoinJoinAsync(KeyManager keyManager, CancellationToken cancellationToken)
	{
		using var device = await AcquireAsync(keyManager.MasterFingerprint, cancellationToken).ConfigureAwait(false);
		var coinJoinAccountKeyPath = TrezorDevice.GetCoinJoinAccountKeyPath(_network);
		var coinJoinExtPubKey = await device.GetCoinJoinXpubAsync(coinJoinAccountKeyPath, _network, cancellationToken).ConfigureAwait(false);

		keyManager.SetCoinJoinAccount(coinJoinAccountKeyPath, coinJoinExtPubKey);
	}

	public async Task<PSBT?> TrySignTransactionAsync(KeyManager keyManager, PSBT psbt, SmartTransaction transaction, CancellationToken cancellationToken)
	{
		// A wallet with a coinjoin account signs everything over the bridge, so that one device session serves
		// sends and coinjoins alike. Everything else is left to HWI.
		if (!keyManager.UsesSlip25CoinJoinAccount())
		{
			return null;
		}

		return await SignOverBridgeAsync(keyManager, psbt, transaction, cancellationToken).ConfigureAwait(false);
	}

	public async Task<bool> TryDisplayAddressAsync(KeyManager keyManager, KeyPath fullKeyPath, BitcoinAddress expectedAddress, CancellationToken cancellationToken)
	{
		// A coinjoin account address needs the UnlockPath that only the bridge can send, and the bridge holds
		// the device anyway - so both accounts of such a wallet are verified over the bridge.
		if (!keyManager.UsesSlip25CoinJoinAccount())
		{
			return false;
		}

		using var device = await AcquireAsync(keyManager.MasterFingerprint, cancellationToken).ConfigureAwait(false);
		var shownAddress = await device.ShowAddressAsync(fullKeyPath, _network, cancellationToken).ConfigureAwait(false);
		if (shownAddress != expectedAddress.ToString())
		{
			throw new InvalidOperationException("The device shows a different address than the wallet. Do not use either of them.");
		}

		return true;
	}

	public async Task<IKeyChain> AuthorizeCoinJoinAsync(
		KeyManager keyManager,
		IKeyChain? existingKeyChain,
		string coordinatorIdentifier,
		int maxRounds,
		FeeRate maxMiningFeeRate,
		CancellationToken cancellationToken)
	{
		var keyChain = existingKeyChain as TrezorKeyChain;
		if (keyChain is not null && !await keyChain.Device.IsSessionAliveAsync(cancellationToken).ConfigureAwait(false))
		{
			// The bridge session died under the wallet: the bridge was restarted, or dropped the device after a
			// USB error, or the wallet was stopped and disposed its transport. Nothing reported that at the time,
			// and every call on the old session would fail forever - so let go of it and acquire the device anew.
			Logger.LogInfo("The Trezor bridge session of this wallet is gone, acquiring the device again.");
			keyChain.Dispose();
			keyChain = null;
		}

		if (keyChain is null)
		{
			var device = await AcquireAsync(keyManager.MasterFingerprint, cancellationToken).ConfigureAwait(false);
			keyChain = new TrezorKeyChain(device, keyManager);
		}

		try
		{
			await keyChain.Device
				.AuthorizeCoinJoinAsync(coordinatorIdentifier, maxRounds, maxMiningFeeRate, keyManager.TaprootAccountKeyPath, _network, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (TrezorException e)
		{
			throw new HardwareWalletException(e.Message, e);
		}

		return keyChain;
	}

	/// <summary>Acquires the device, starting a transport for it first when we own one.</summary>
	private async Task<TrezorDevice> AcquireAsync(HDFingerprint? masterFingerprint, CancellationToken cancellationToken)
	{
		// Start the bridge if it is not already running, so that coinjoin authorization, signing and reading the
		// coinjoin account work without the user launching anything (and after a detection borrowed the device).
		await _bridge.EnsureRunningAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			return await TrezorDevice.FindAsync(masterFingerprint, cancellationToken).ConfigureAwait(false);
		}
		catch (TrezorBridgeNotFoundException e)
		{
			throw new HardwareWalletTransportNotFoundException(e.Message, e);
		}
		catch (TrezorDeviceNotFoundException e)
		{
			throw new HardwareWalletNotFoundException(e.Message, e);
		}
		catch (TrezorException e)
		{
			throw new HardwareWalletException(e.Message, e);
		}
	}

	private async Task<KeyManager> ReadAccountsAsync(TrezorDevice device, HDFingerprint fingerprint, string walletFilePath, bool enableCoinjoin, CancellationToken cancellationToken)
	{
		var segwitAccountKeyPath = KeyManager.GetAccountKeyPath(_network, ScriptPubKeyType.Segwit);
		var segwitExtPubKey = await device.GetSegwitAccountXpubAsync(segwitAccountKeyPath, _network, cancellationToken).ConfigureAwait(false);

		KeyManager keyManager;
		if (enableCoinjoin)
		{
			var coinJoinAccountKeyPath = TrezorDevice.GetCoinJoinAccountKeyPath(_network);
			var coinJoinExtPubKey = await device.GetCoinJoinXpubAsync(coinJoinAccountKeyPath, _network, cancellationToken).ConfigureAwait(false);
			keyManager = KeyManager.CreateNewHardwareWalletWatchOnly(fingerprint, segwitExtPubKey, coinJoinExtPubKey, null, null, _network, walletFilePath, coinJoinAccountKeyPath);

			// Only coins of the coinjoin account can join rounds, so hand out its addresses by default; the
			// regular account stays available for deposits that should not be coinjoined.
			keyManager.DefaultReceiveScriptType = ScriptPubKeyType.TaprootBIP86;
		}
		else
		{
			keyManager = KeyManager.CreateNewHardwareWalletWatchOnly(fingerprint, segwitExtPubKey, null, null, null, _network, walletFilePath);
		}

		keyManager.SetIcon(WalletType.Trezor);
		return keyManager;
	}

	private async Task<PSBT> SignOverBridgeAsync(KeyManager keyManager, PSBT psbt, SmartTransaction transaction, CancellationToken cancellationToken)
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

		using var device = await AcquireAsync(keyManager.MasterFingerprint, cancellationToken).ConfigureAwait(false);
		var signatures = await device.SignTransactionAsync(
			inputs,
			outputs,
			(uint)globalTransaction.Version,
			globalTransaction.LockTime.Value,
			_network,
			unlockCoinJoinAccount: spendsCoinJoinAccount,
			previousTransactions,
			cancellationToken).ConfigureAwait(false);

		var signedPsbt = psbt.Clone();
		foreach (var signature in signatures)
		{
			var index = signature.Key;
			signedPsbt.Inputs[index].FinalScriptWitness = inputs[index].ScriptType == TrezorInputScriptType.SpendTaproot
				? new WitScript(Op.GetPushOp(signature.Value))
				: BuildSegwitWitness(keyManager, psbt.Inputs[index], signature.Value);
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
