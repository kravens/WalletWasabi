using System.Diagnostics.CodeAnalysis;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Hwi;
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

	public HardwareWalletService(Network network)
	{
		_network = network;
		_trezor = new TrezorBackend(network, status =>
		{
			_transportStatus = status;
			TransportStatusChanged?.Invoke(this, status);
		});
		_backends = new IHardwareWalletBackend[] { _trezor, new ColdcardBackend(network) }.ToDictionary(backend => backend.Vendor);
	}

	private readonly Network _network;
	private readonly TrezorBackend _trezor;
	private readonly Dictionary<HardwareCoinJoinVendor, IHardwareWalletBackend> _backends;
	private HardwareWalletTransport _transportStatus;

	/// <summary>Raised when the transport used to reach the device changes, so the UI can show it.</summary>
	public event EventHandler<HardwareWalletTransport>? TransportStatusChanged;

	/// <summary>How the device is currently reached.</summary>
	public HardwareWalletTransport TransportStatus => _transportStatus;

	/// <summary>Whether a device that signs coinjoins can be reached, to warn before offering it.</summary>
	public async Task<bool> IsCoinJoinTransportAvailableAsync(CancellationToken cancellationToken)
	{
		foreach (var backend in _backends.Values)
		{
			if (await backend.IsTransportAvailableAsync(cancellationToken).ConfigureAwait(false))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>The backend for this wallet's vendor, or null when no device signs its coinjoins.</summary>
	private IHardwareWalletBackend? BackendFor(KeyManager keyManager) =>
		_backends.GetValueOrDefault(keyManager.GetCoinJoinVendor());

	/// <summary>Whether this wallet's coinjoins are signed by a device rather than by keys we hold.</summary>
	public static bool IsRemoteSigner(KeyManager keyManager) => keyManager.IsHardwareCoinJoinWallet();

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

	/// <summary>How long a device may take to confirm a coinjoin authorization (one hold-to-confirm).</summary>
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

	/// <summary>Whether a detected device can act as a coinjoin remote signer, to offer it while importing.</summary>
	public static bool CanSignCoinJoins(HwiEnumerateEntry device) => device.Model.SupportsCoinJoin();

	/// <summary>Lists the connected devices. Releases a transport we own first, since HWI needs the device itself.</summary>
	public async Task<HwiEnumerateEntry[]> DetectAsync(CancellationToken cancellationToken)
	{
		foreach (var backend in _backends.Values)
		{
			backend.Release();
		}

		using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);

		var detectedHardwareWallets = (await new HwiClient(_network).EnumerateAsync(timeoutCts.Token).ConfigureAwait(false)).ToArray();

		cancellationToken.ThrowIfCancellationRequested();

		return detectedHardwareWallets;
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

		using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
		using var genCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);

		var vendor = device.Model.VendorOf();
		if (enableCoinjoin && _backends.GetValueOrDefault(vendor) is { } backend
			&& await backend.TryImportAsync(fingerprint, walletFilePath, enableCoinjoin: true, addressToConfirm, genCts.Token).ConfigureAwait(false) is { } fromDevice)
		{
			return fromDevice;
		}

		var segwitExtPubKey = await new HwiClient(_network).GetXpubAsync(device.Model, device.Path, KeyManager.GetAccountKeyPath(_network, ScriptPubKeyType.Segwit), genCts.Token).ConfigureAwait(false);
		var keyManager = KeyManager.CreateNewHardwareWalletWatchOnly(fingerprint, segwitExtPubKey, null, null, null, _network, walletFilePath);
		keyManager.SetIcon(device.WalletType);

		// A vendor that signs from the wallet's ordinary accounts leaves no trace in the key path, so what
		// signs this wallet has to be written down. Trezor is left alone: its account shape says so already.
		if (enableCoinjoin && vendor is not (HardwareCoinJoinVendor.None or HardwareCoinJoinVendor.Trezor))
		{
			keyManager.CoinJoinVendor = vendor;
		}

		return keyManager;
	}

	/// <summary>Imports the connected device without detecting it over HWI first, which a headless host cannot do.</summary>
	public async Task<KeyManager> ImportConnectedAsync(string walletFilePath, bool enableCoinjoin, IProgress<BitcoinAddress>? addressToConfirm, CancellationToken cancellationToken)
	{
		// A vendor with a transport of its own can find its device without HWI, which a headless host cannot
		// drive for the accounts it needs. Ask each in turn; the others are found by enumerating below.
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
				// Another vendor's device may still be the one that is plugged in.
				lastError = e;
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

	/// <summary>Reads a Trezor's accounts in one bridge session, proving each on the device first. Here so the check can be exercised without a bridge.</summary>
	internal Task<KeyManager> ReadAccountsAsync(TrezorDevice device, HDFingerprint fingerprint, string walletFilePath, bool enableCoinjoin, IProgress<BitcoinAddress>? addressToConfirm, CancellationToken cancellationToken) =>
		_trezor.ReadAccountsAsync(device, fingerprint, walletFilePath, enableCoinjoin, addressToConfirm, cancellationToken);

	/// <summary>
	/// Adds a coinjoin account to an already imported watch-only wallet. The device confirms it and then shows
	/// its first address for the user to check, as an import does. No-op if the wallet already has one.
	/// </summary>
	public async Task EnableCoinJoinAsync(KeyManager keyManager, IProgress<BitcoinAddress>? addressToConfirm, CancellationToken cancellationToken)
	{
		if (!keyManager.IsHardwareWallet)
		{
			throw new InvalidOperationException("Only a hardware wallet can have a coinjoin account added.");
		}
		if (IsRemoteSigner(keyManager))
		{
			return;
		}

		// The wallet is not a coinjoin wallet yet, so there is no recorded vendor to ask; the device that
		// holds its keys decides. Only Trezor can be enabled after the fact today, by adding the account.
		await _trezor.EnableCoinJoinAsync(keyManager, addressToConfirm, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Signs a transaction with the device: over the vendor's own transport when it wants to, otherwise through
	/// HWI, which needs the device for itself and therefore borrows it from any transport we own.
	/// </summary>
	/// <param name="transaction">The transaction being signed; its wallet inputs carry the previous transactions the device asks for.</param>
	public async Task<PSBT> SignTransactionAsync(KeyManager keyManager, PSBT psbt, SmartTransaction transaction, CancellationToken cancellationToken)
	{
		AssertKeysAreOnADevice(keyManager);

		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(SigningTimeout(transaction.WalletInputs.Count));
		cancellationToken = timeout.Token;

		if (BackendFor(keyManager) is { } backend
			&& await backend.TrySignTransactionAsync(keyManager, psbt, transaction, cancellationToken).ConfigureAwait(false) is { } vendorSigned)
		{
			return vendorSigned;
		}

		// Only put a borrowed transport back if we actually took it; starting one for a wallet that does not
		// need it would hold the device for nothing. The device forgets a coinjoin authorization when its
		// session ends, so the next coinjoin start asks for a new confirmation.
		var borrowedFrom = _backends.Values.FirstOrDefault(b => b.SharesTransportWith(keyManager) && b.Release());
		try
		{
			var signedPsbt = await new HwiClient(_network).SignTxAsync(keyManager.MasterFingerprint!.Value, psbt, cancellationToken).ConfigureAwait(false);
			AssertSpendsWhatWasBuilt(psbt, signedPsbt);
			return signedPsbt;
		}
		finally
		{
			if (borrowedFrom is not null)
			{
				await borrowedFrom.EnsureReadyAsync(CancellationToken.None).ConfigureAwait(false);
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
			if (BackendFor(keyManager) is { } backend
				&& await backend.TryDisplayAddressAsync(keyManager, fullKeyPath, expectedAddress, linkedCts.Token).ConfigureAwait(false))
			{
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

	/// <summary>
	/// Asks the device to authorize a batch of coinjoin rounds, shown on its screen with the fee cap and confirmed
	/// physically. The returned key chain then signs those rounds without further interaction.
	/// </summary>
	/// <param name="existingKeyChain">The wallet's current key chain, reused when it already holds the device.</param>
	public async Task<IKeyChain> AuthorizeCoinJoinAsync(
		KeyManager keyManager,
		IKeyChain? existingKeyChain,
		string coordinatorIdentifier,
		int maxRounds,
		FeeRate maxMiningFeeRate,
		CancellationToken cancellationToken)
	{
		if (!IsRemoteSigner(keyManager))
		{
			throw new NotSupportedException("This wallet has no coinjoin account, so no device can authorize its coinjoins.");
		}

		var backend = BackendFor(keyManager)
			?? throw new NotSupportedException($"No support for authorizing coinjoins on a {keyManager.GetCoinJoinVendor()} device.");

		return await backend
			.AuthorizeCoinJoinAsync(keyManager, existingKeyChain, coordinatorIdentifier, maxRounds, maxMiningFeeRate, cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>Makes sure the device of this wallet can be reached, if it needs a transport of ours at all.</summary>
	public async Task EnsureReadyAsync(KeyManager keyManager, CancellationToken cancellationToken)
	{
		if (BackendFor(keyManager) is { } backend)
		{
			await backend.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>Hands the device back, for when this wallet no longer needs it.</summary>
	public void Release(KeyManager keyManager)
	{
		BackendFor(keyManager)?.Release();
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
