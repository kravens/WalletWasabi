using System.Diagnostics.CodeAnalysis;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Hwi;
using WalletWasabi.Hwi.Coldcard;
using WalletWasabi.Hwi.Models;
using WalletWasabi.Hwi.Trezor;
using WalletWasabi.Wallets.Backends;
using WalletWasabi.WabiSabi.Client;

namespace WalletWasabi.Wallets;

/// <summary>
/// Every operation that talks to a hardware wallet. A device is reachable over two mutually exclusive transports,
/// HWI (which takes the USB device for itself) and a vendor's own (the Trezor Bridge is the only way to a SLIP-25
/// coinjoin account); choosing between them and handing the device over is this service's business, not its callers'.
/// </summary>
public class HardwareWalletService : IDisposable
{
	/// <summary>Where to get a bridge when none is running. Callers may show this to the user.</summary>
	public static string BridgeDownloadUrl => TrezorBridgeProcess.SuiteDownloadUrl;

	/// <param name="walletCoinJoiningOnDevice">Names the wallet whose coinjoin is using its device right now, if any.</param>
	public HardwareWalletService(Network network, Func<string?>? walletCoinJoiningOnDevice = null)
	{
		_network = network;
		_walletCoinJoiningOnDevice = walletCoinJoiningOnDevice ?? (() => null);
		_trezor = new TrezorBackend(network, status => TransportStatusChanged?.Invoke(this, status));
		_backends = new IHardwareWalletBackend[] { _trezor, new ColdcardBackend(), new PassportBackend(network), new KruxBackend(network) }.ToDictionary(backend => backend.Vendor);
	}

	private readonly Network _network;
	private readonly Func<string?> _walletCoinJoiningOnDevice;
	private readonly TrezorBackend _trezor;
	private readonly Dictionary<HardwareCoinJoinVendor, IHardwareWalletBackend> _backends;

	/// <summary>Raised when the transport used to reach the device changes, so the UI can show it.</summary>
	public event EventHandler<HardwareWalletTransport>? TransportStatusChanged;

	/// <summary>How the device is currently reached.</summary>
	public HardwareWalletTransport TransportStatus => _trezor.TransportStatus;

	/// <summary>Whether the transport this device signs coinjoins over can be reached, to warn before offering it. Answers within a few seconds.</summary>
	public async Task<bool> IsCoinJoinTransportAvailableAsync(HwiEnumerateEntry device, CancellationToken cancellationToken)
	{
		if (_backends.GetValueOrDefault(device.Model.VendorOf()) is not { } backend)
		{
			return false;
		}

		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(5));
		return await backend.IsTransportAvailableAsync(timeout.Token).ConfigureAwait(false);
	}

	/// <summary>The backend for this wallet's vendor, or null when no device signs its coinjoins.</summary>
	private IHardwareWalletBackend? BackendFor(KeyManager keyManager) =>
		_backends.GetValueOrDefault(keyManager.CoinJoinVendor);

	/// <summary>Most coinjoin rounds one device authorization may cover; the firmware refuses more under its own safety checks.</summary>
	public const int MaxAuthorizationRounds = 500;

	/// <summary>Highest mining fee rate (sat/vByte) a device may be authorized to sign coinjoins at; above this a cap is no cap.</summary>
	public const decimal MaxAuthorizationFeeRate = 10_000m;

	/// <summary>Whether the number of rounds is one a device can be asked to approve; the reason is written for a settings field to show as is.</summary>
	public static bool TryValidateMaxRounds(int? maxRounds, [NotNullWhen(false)] out string? error)
	{
		if (maxRounds is < 1 or > MaxAuthorizationRounds)
		{
			error = $"Must be a whole number between 1 and {MaxAuthorizationRounds}.";
			return false;
		}

		error = null;
		return true;
	}

	/// <summary>Whether the mining fee rate is one a device can be authorized to sign at, with the reason when it is not.</summary>
	public static bool TryValidateMaxMiningFeeRate(decimal? maxMiningFeeRate, [NotNullWhen(false)] out string? error)
	{
		if (maxMiningFeeRate is <= 0m or > MaxAuthorizationFeeRate)
		{
			error = $"Must be a fee rate above 0 and at most {MaxAuthorizationFeeRate} sat/vByte.";
			return false;
		}

		error = null;
		return true;
	}

	/// <summary>How long a device may take to sign a transaction: a person confirms every output on its screen.</summary>
	public static TimeSpan SigningTimeout(int inputCount) =>
		TimeSpan.FromMinutes(3) + TimeSpan.FromMinutes(inputCount / 10);

	/// <summary>How long a device may take for one confirmation on its screen: a coinjoin authorization, or an account it shows while importing.</summary>
	public static TimeSpan AuthorizationTimeout => TimeSpan.FromMinutes(3);

	/// <summary>Throws when the limits are outside what a device can be asked to approve.</summary>
	public static void AssertAuthorizationLimits(int? maxRounds, decimal? maxMiningFeeRate)
	{
		if (!TryValidateMaxRounds(maxRounds, out var roundsError))
		{
			throw new ArgumentOutOfRangeException(nameof(maxRounds), maxRounds, roundsError);
		}

		if (!TryValidateMaxMiningFeeRate(maxMiningFeeRate, out var feeRateError))
		{
			throw new ArgumentOutOfRangeException(nameof(maxMiningFeeRate), maxMiningFeeRate, feeRateError);
		}
	}

	/// <summary>Whether this wallet's device enforces limits of its own beyond the round budget - how much value may leave, how often, how large a round must be. Only a device running a policy has them.</summary>
	public static bool HasDevicePolicyLimits(KeyManager keyManager) =>
		keyManager.CoinJoinVendor is HardwareCoinJoinVendor.Coldcard;

	/// <summary>
	/// Whether the wallet's saved limits differ from the policy its device is actually enforcing. A device
	/// policy cannot be changed while it runs - deliberately, since a host that could end it could also drop
	/// every limit - so a value edited afterwards is saved and simply not applied until the device restarts.
	/// False before any policy has been approved, when there is nothing to disagree with.
	/// </summary>
	public static bool IsDevicePolicyOutOfSync(KeyManager keyManager) =>
		HasDevicePolicyLimits(keyManager)
		&& keyManager.ColdcardApprovedPolicyFingerprint is { Length: > 0 } approved
		&& approved != ColdcardHsmPolicy.Fingerprint(ColdcardBackend.ComposePolicy(keyManager));

	/// <summary>
	/// Which of the coinjoin limits this wallet's device enforces and which Wasabi does, in words a person
	/// can act on. Vendors differ and must not be misrepresented: telling a user their device confirms limits
	/// it never sees would be a false claim.
	/// </summary>
	public static string DescribeLimitEnforcement(KeyManager keyManager) =>
		keyManager.CoinJoinVendor switch
		{
			HardwareCoinJoinVendor.Trezor => "Shown on the device and confirmed there; the device enforces both.",
			HardwareCoinJoinVendor.Coldcard =>
				"Enforced by the device policy: the fee-rate cap, how much of your value may leave in a single "
				+ "transaction, how many transactions it signs in total and per period, and the smallest round it signs.",
			HardwareCoinJoinVendor.Krux =>
				"The budget you approved on the device - rounds, sats per round and in total - is enforced by the "
				+ "device; the fee-rate cap and the round budget below are enforced by Wasabi.",
			HardwareCoinJoinVendor.PassportPrime =>
				"The round budget, and a total fee budget derived from the fee-rate cap, are shown on the device and "
				+ "enforced there; the fee-rate cap itself is enforced by Wasabi.",
			_ => "",
		};

	/// <summary>Whether the cap on value leaving per transaction is one a device policy can hold: a round costs the wallet its fee share, so a few thousand sats is the floor, and past a whole bitcoin it stops being a cap.</summary>
	public static bool TryValidateMaxSatsLeaving(long? sats, [NotNullWhen(false)] out string? error) =>
		Check(sats is null or (>= 1_000 and <= 100_000_000), "Must be between 1,000 and 100,000,000 sats.", out error);

	public static bool TryValidateMaxTransactionsPerPeriod(int? count, [NotNullWhen(false)] out string? error) =>
		Check(count is null or (>= 1 and <= MaxAuthorizationRounds), $"Must be a whole number between 1 and {MaxAuthorizationRounds}.", out error);

	/// <summary>0 turns the device-side floor off; above that it is the fewest round inputs the device signs with.</summary>
	public static bool TryValidateMinRoundInputs(int? inputs, [NotNullWhen(false)] out string? error) =>
		Check(inputs is null or (>= 0 and <= MaxAuthorizationRounds), $"Must be a whole number between 0 and {MaxAuthorizationRounds}.", out error);

	/// <summary>Sets the limits a device policy enforces; whatever is left out keeps its value. Applies from the next policy the device approves.</summary>
	public static void SetDevicePolicyLimits(KeyManager keyManager, long? maxSatsLeaving, int? maxTransactionsPerPeriod, int? minRoundInputs)
	{
		if (!HasDevicePolicyLimits(keyManager))
		{
			throw new InvalidOperationException("The device of this wallet runs no policy of its own, so it has no such limits.");
		}
		if (!TryValidateMaxSatsLeaving(maxSatsLeaving, out var error)
			|| !TryValidateMaxTransactionsPerPeriod(maxTransactionsPerPeriod, out error)
			|| !TryValidateMinRoundInputs(minRoundInputs, out error))
		{
			throw new ArgumentOutOfRangeException(nameof(keyManager), error);
		}

		keyManager.ColdcardMaxSatsLeaving = maxSatsLeaving ?? keyManager.ColdcardMaxSatsLeaving;
		keyManager.ColdcardMaxTransactionsPerPeriod = maxTransactionsPerPeriod ?? keyManager.ColdcardMaxTransactionsPerPeriod;
		keyManager.ColdcardMinInputs = minRoundInputs ?? keyManager.ColdcardMinInputs;
	}

	private static bool Check(bool valid, string message, out string? error)
	{
		error = valid ? null : message;
		return valid;
	}

	/// <summary>Whether a detected device can act as a coinjoin remote signer, to offer it while importing.</summary>
	public static bool CanSignCoinJoins(HwiEnumerateEntry device) => device.Model.SupportsCoinJoin();

	private static CancellationTokenSource ConfirmationTimeout(CancellationToken cancellationToken)
	{
		var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		cts.CancelAfter(AuthorizationTimeout);
		return cts;
	}

	/// <summary>Lists the connected devices. Releases a transport we own first, since HWI needs the device itself.</summary>
	public async Task<HwiEnumerateEntry[]> DetectAsync(CancellationToken cancellationToken)
	{
		AssertNoCoinJoinOnDevice();
		_trezor.Release();

		using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);

		var detected = (await new HwiClient(_network).EnumerateAsync(timeoutCts.Token).ConfigureAwait(false))
			.Where(device => !device.IsFailedProbe())
			.ToList();

		cancellationToken.ThrowIfCancellationRequested();

		// Devices HWI cannot see are found over their vendor's own transport.
		foreach (var backend in _backends.Values)
		{
			if (await backend.TryDetectAsync(timeoutCts.Token).ConfigureAwait(false) is { } ownDevice)
			{
				detected.Add(ownDevice);
			}
		}

		return [.. detected];
	}

	/// <summary>Runs the device's initial setup, for a device that reports it has no seed yet.</summary>
	public async Task InitializeAsync(HwiEnumerateEntry device, CancellationToken cancellationToken)
	{
		using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(21));
		using var initCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);

		// Trezor T doesn't require interactive mode.
		var interactiveMode = !(device.Model == HardwareWalletModels.Trezor_T || device.Model == HardwareWalletModels.Trezor_T_Simulator);

		try
		{
			await new HwiClient(_network).SetupAsync(device.Model, device.Path, interactiveMode, initCts.Token).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			Logger.LogError(ex);
		}
	}

	/// <summary>Imports an already detected device as a watch-only wallet.</summary>
	/// <param name="enableCoinjoin">Also read the coinjoin account, so the device can sign coinjoins; the device asks for a confirmation.</param>
	/// <param name="addressToConfirm">Told each address the device is about to show, so the caller can put it next to the device for the user to compare.</param>
	public async Task<KeyManager> ImportAsync(HwiEnumerateEntry device, string walletFilePath, bool enableCoinjoin, IProgress<BitcoinAddress>? addressToConfirm, CancellationToken cancellationToken)
	{
		if (device.Fingerprint is not { } fingerprint)
		{
			throw new InvalidOperationException("The device did not report a master fingerprint.");
		}
		AssertNoCoinJoinOnDevice();

		using var genCts = ConfirmationTimeout(cancellationToken);

		// The vendor's own transport reads what HWI cannot (a coinjoin account, or a device HWI does not know); it declines when HWI is enough.
		var vendor = device.Model.VendorOf();
		if (_backends.GetValueOrDefault(vendor) is { } backend
			&& await backend.TryImportAsync(fingerprint, walletFilePath, enableCoinjoin, addressToConfirm, genCts.Token).ConfigureAwait(false) is { } fromDevice)
		{
			return fromDevice;
		}

		var segwitExtPubKey = await new HwiClient(_network).GetXpubAsync(device.Model, device.Path, KeyManager.GetAccountKeyPath(_network, ScriptPubKeyType.Segwit), genCts.Token).ConfigureAwait(false);
		var keyManager = KeyManager.CreateNewHardwareWalletWatchOnly(fingerprint, segwitExtPubKey, null, null, null, _network, walletFilePath);
		keyManager.SetIcon(device.WalletType);
		if (enableCoinjoin)
		{
			keyManager.CoinJoinVendor = vendor;
		}

		return keyManager;
	}

	/// <summary>Imports the connected device without detecting it over HWI first, which a headless host cannot do.</summary>
	public async Task<KeyManager> ImportConnectedAsync(string walletFilePath, bool enableCoinjoin, IProgress<BitcoinAddress>? addressToConfirm, CancellationToken cancellationToken)
	{
		AssertNoCoinJoinOnDevice();
		using var timeout = ConfirmationTimeout(cancellationToken);
		cancellationToken = timeout.Token;

		// A vendor with a transport of its own finds its device without HWI. Ask each in turn; the rest are enumerated below.
		HardwareWalletException? lastError = null;
		foreach (var backend in _backends.Values)
		{
			try
			{
				if (await backend.TryImportAsync(masterFingerprint: null, walletFilePath, enableCoinjoin, addressToConfirm, cancellationToken).ConfigureAwait(false) is { } fromDevice)
				{
					return fromDevice;
				}
			}
			catch (HardwareWalletException e)
			{
				lastError = e; // Another vendor's device may still be the one that is plugged in.
			}
		}

		var detected = (await DetectAsync(cancellationToken).ConfigureAwait(false))
			.Where(device => device.Fingerprint is not null && (!enableCoinjoin || CanSignCoinJoins(device)))
			.ToArray();

		if (detected.Length == 0)
		{
			throw lastError ?? new HardwareWalletNotFoundException("No device that can be imported is connected.");
		}

		if (detected.Length > 1)
		{
			// Picking one for the user could import the wrong wallet, which is not ours to guess at.
			throw new HardwareWalletException("More than one device is connected. Leave only the one to import.");
		}

		return await ImportAsync(detected[0], walletFilePath, enableCoinjoin, addressToConfirm, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Whether a coinjoin account can be added: a wallet another vendor already signs coinjoins for has nothing to enable.</summary>
	public static bool CanEnableCoinJoin(KeyManager keyManager) =>
		keyManager.CanAddCoinJoinAccount && keyManager.CoinJoinVendor is HardwareCoinJoinVendor.None;

	/// <summary>Adds a coinjoin account to an imported watch-only wallet; the device confirms it and shows its first address to check, as an import does.</summary>
	public async Task EnableCoinJoinAsync(KeyManager keyManager, IProgress<BitcoinAddress>? addressToConfirm, CancellationToken cancellationToken)
	{
		if (keyManager.HasCoinJoinAccount)
		{
			return;
		}
		if (!CanEnableCoinJoin(keyManager))
		{
			throw new InvalidOperationException("Only a hardware wallet without a taproot account or a coinjoin signer can have a coinjoin account added.");
		}
		var detected = await DetectAsync(cancellationToken).ConfigureAwait(false);
		await EnableCoinJoinAsync(keyManager, detected, addressToConfirm, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Enables coinjoin the way the connected device of this wallet, picked from <paramref name="detected"/> by fingerprint, signs them.</summary>
	internal async Task EnableCoinJoinAsync(KeyManager keyManager, IEnumerable<HwiEnumerateEntry> detected, IProgress<BitcoinAddress>? addressToConfirm, CancellationToken cancellationToken)
	{
		var device = detected.FirstOrDefault(device => device.Fingerprint == keyManager.MasterFingerprint)
			?? throw new HardwareWalletNotFoundException("The device of this wallet is not connected. Connect and unlock it, then try again.");

		switch (device.Model.VendorOf())
		{
			case HardwareCoinJoinVendor.Trezor:
				using (var timeout = ConfirmationTimeout(cancellationToken))
				{
					await _trezor.EnableCoinJoinAsync(keyManager, addressToConfirm, timeout.Token).ConfigureAwait(false);
				}
				break;

			case HardwareCoinJoinVendor.Coldcard:
				// Signs from the wallet's own segwit account under an HSM policy it approves at the first coinjoin; there is no account to add.
				keyManager.CoinJoinVendor = HardwareCoinJoinVendor.Coldcard;
				keyManager.ToFile();
				break;

			case HardwareCoinJoinVendor.None:
				throw new HardwareWalletException("This device cannot sign coinjoins.");

			default:
				// Passport and Krux read their own accounts at import.
				throw new HardwareWalletException("Coinjoin for this device is set up at import. Import the wallet again with coinjoin on.");
		}
	}

	/// <summary>Whether this wallet can add a taproot account later: a Coldcard is imported with segwit only, and signs taproot only on Edge firmware.</summary>
	public static bool CanEnableTaproot(KeyManager keyManager) =>
		keyManager.CoinJoinVendor is HardwareCoinJoinVendor.Coldcard && keyManager.CanAddCoinJoinAccount;

	/// <summary>Adds the device's taproot account to a Coldcard coinjoin wallet. The device policy changes with it, so the Coldcard approves a new one at the next coinjoin.</summary>
	public async Task EnableTaprootAsync(KeyManager keyManager, CancellationToken cancellationToken)
	{
		if (!CanEnableTaproot(keyManager))
		{
			throw new InvalidOperationException("Only a Coldcard coinjoin wallet without a taproot account can have one added.");
		}
		AssertNoCoinJoinOnDevice();

		await Task.Run(() => ColdcardBackend.AddTaprootAccount(keyManager), cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Signs with the device: a wallet with a coinjoin account signs everything over the bridge (one device session
	/// for sends and coinjoins alike), every other wallet through HWI, which borrows the device from any bridge we own.
	/// </summary>
	/// <param name="transaction">The transaction being signed; its wallet inputs carry the previous transactions the device asks for.</param>
	public async Task<PSBT> SignTransactionAsync(KeyManager keyManager, PSBT psbt, SmartTransaction transaction, CancellationToken cancellationToken)
	{
		AssertKeysAreOnADevice(keyManager);

		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(SigningTimeout(transaction.WalletInputs.Count));
		cancellationToken = timeout.Token;

		if (keyManager.HasCoinJoinAccount)
		{
			return await _trezor.SignOverBridgeAsync(keyManager, psbt, transaction, cancellationToken).ConfigureAwait(false);
		}

		// A Trezor wallet shares the bridge, so HWI has to borrow the device from a bridge of ours; only put that
		// bridge back if we actually took it. The device forgets a coinjoin authorization when its session ends,
		// so the next coinjoin start asks for a new confirmation.
		bool isTrezor = _trezor.SharesTransportWith(keyManager);
		if (isTrezor)
		{
			AssertNoCoinJoinOnDevice();
		}
		bool borrowedFromOurBridge = isTrezor && _trezor.Release();
		try
		{
			var signedPsbt = await new HwiClient(_network).SignTxAsync(keyManager.MasterFingerprint!.Value, psbt, cancellationToken).ConfigureAwait(false);
			AssertSpendsWhatWasBuilt(psbt, signedPsbt);
			return signedPsbt;
		}
		finally
		{
			if (borrowedFromOurBridge)
			{
				await _trezor.EnsureReadyAsync(CancellationToken.None).ConfigureAwait(false);
			}
		}
	}

	/// <summary>Shows a receive address on the device screen and verifies that it is the one the wallet expects.</summary>
	public async Task DisplayAddressAsync(KeyManager keyManager, KeyPath fullKeyPath, BitcoinAddress expectedAddress, CancellationToken cancellationToken)
	{
		AssertKeysAreOnADevice(keyManager);

		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
		using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);
		try
		{
			if (keyManager.HasCoinJoinAccount)
			{
				// A coinjoin account address needs the UnlockPath that only the bridge can send, and the bridge
				// holds the device anyway - so both accounts of such a wallet are verified over the bridge.
				await _trezor.DisplayAddressAsync(keyManager, fullKeyPath, expectedAddress, linkedCts.Token).ConfigureAwait(false);
				return;
			}

			await new HwiClient(_network).DisplayAddressAsync(keyManager.MasterFingerprint!.Value, fullKeyPath, linkedCts.Token).ConfigureAwait(false);
		}
		catch (FormatException ex) when (ex.Message.Contains("network") && _network == Network.TestNet)
		{
			// This exception happens every time on TestNet because of Wasabi Keypath handling.
			// The user doesn't need to know about it.
		}
		catch (Exception) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
		{
			throw new InvalidOperationException("The device did not answer in time.");
		}
	}

	/// <summary>Asks the device to authorize the wallet's coinjoin limits; the returned key chain then signs the rounds without further interaction.</summary>
	public Task<IKeyChain> AuthorizeCoinJoinAsync(KeyManager keyManager, string coordinatorIdentifier, CancellationToken cancellationToken)
	{
		var backend = BackendFor(keyManager)
			?? throw new NotSupportedException("No device signs the coinjoins of this wallet, so none can authorize them.");

		return backend.AuthorizeCoinJoinAsync(keyManager, coordinatorIdentifier, cancellationToken);
	}

	/// <summary>Makes sure the device of this wallet can be reached, if it needs a transport of ours at all.</summary>
	public async Task EnsureReadyAsync(KeyManager keyManager, CancellationToken cancellationToken)
	{
		if (keyManager.HasCoinJoinAccount)
		{
			await _trezor.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>Hands the device back, for when this wallet no longer needs it.</summary>
	public void Release(KeyManager keyManager) => BackendFor(keyManager)?.ReleaseFor(keyManager);

	/// <summary>Taking the device away from a coinjoin ends its authorization mid-round, and a round past input registration bans the inputs that fail to sign.</summary>
	private void AssertNoCoinJoinOnDevice()
	{
		if (_walletCoinJoiningOnDevice() is { } walletName)
		{
			throw new HardwareWalletException($"Wallet '{walletName}' is coinjoining with its device. Stop its coinjoin first.");
		}
	}

	/// <summary>Guards the operations that only make sense when a device holds the wallet's keys.</summary>
	private static void AssertKeysAreOnADevice(KeyManager keyManager)
	{
		if (!keyManager.IsHardwareWallet)
		{
			throw new HardwareWalletException("The keys of this wallet are not on a device.");
		}
	}

	/// <summary>Checks that signing, done by another process, did not change what is spent or where it goes: the user only approved what the device displayed.</summary>
	public static void AssertSpendsWhatWasBuilt(PSBT built, PSBT signed)
	{
		var before = built.GetGlobalTransaction();
		var after = signed.GetGlobalTransaction();

		bool sameInputs = before.Inputs.Count == after.Inputs.Count
			&& before.Inputs.Select(x => x.PrevOut).SequenceEqual(after.Inputs.Select(x => x.PrevOut));

		bool sameOutputs = before.Outputs.Count == after.Outputs.Count
			&& before.Outputs.Zip(after.Outputs).All(pair => pair.First.Value == pair.Second.Value && pair.First.ScriptPubKey == pair.Second.ScriptPubKey);

		if (!sameInputs || !sameOutputs)
		{
			throw new HardwareWalletException("The signed transaction does not match the one that was built. It was not broadcast.");
		}
	}

	public void Dispose()
	{
		foreach (var backend in _backends.Values)
		{
			backend.Dispose();
		}
	}
}
