using WalletWasabi.Hwi;
using WalletWasabi.Hwi.Krux;
using WalletWasabi.Hwi.Models;
using WalletWasabi.Models;
using WalletWasabi.WabiSabi.Client;

namespace WalletWasabi.Wallets.Backends;

/// <summary>
/// Krux, or its SabiSigner fork, reached through the external kruxd bridge. Nothing is authorized from here:
/// the user approves the signing session on the device itself, which by design the host cannot alter. This
/// confirms the bridge serves this wallet's device and that the approved session still has rounds left, and
/// imports accounts the bridge reads from the device, since HWI cannot see either device.
/// </summary>
internal class KruxBackend : IHardwareWalletBackend
{
	public KruxBackend(Network network, Func<KruxClient>? newClient = null)
	{
		_network = network;
		_newClient = newClient ?? (() => new KruxClient());
	}

	private readonly Network _network;
	private readonly Func<KruxClient> _newClient;

	public HardwareCoinJoinVendor Vendor => HardwareCoinJoinVendor.Krux;

	public bool IsUnknownToHwi => true;

	public async Task<bool> IsTransportAvailableAsync(CancellationToken cancellationToken) =>
		await TryGetInfoAsync(cancellationToken).ConfigureAwait(false) is not null;

	public async Task<HwiEnumerateEntry?> TryDetectAsync(CancellationToken cancellationToken)
	{
		if (await TryGetInfoAsync(cancellationToken).ConfigureAwait(false) is not { } info)
		{
			return null;
		}

		var model = info.Device == "sabi" ? HardwareWalletModels.SabiSigner : HardwareWalletModels.Krux;
		return new HwiEnumerateEntry(model, KruxClient.DefaultBridgeUri, null, info.Fingerprint, false, false, null, null);
	}

	/// <summary>The bridge's answer within a couple of seconds, or null: a probe must not stall a screen.</summary>
	private async Task<KruxClient.KruxInfo?> TryGetInfoAsync(CancellationToken cancellationToken)
	{
		using var client = _newClient();
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		cts.CancelAfter(TimeSpan.FromSeconds(2));
		try
		{
			return await client.GetInfoAsync(cts.Token).ConfigureAwait(false);
		}
		catch (Exception e) when (e is HardwareWalletException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
		{
			Logger.LogDebug($"No kruxd answering: {e.Message}");
			return null;
		}
	}

	/// <summary>
	/// Reads both accounts over the bridge, pinned to the fingerprint of the session the device approved: a
	/// bridge handing out another seed's accounts is refused. The device cannot be made to show an address
	/// from here, so the first receive address is reported for the user to check against the device by hand.
	/// </summary>
	public async Task<KeyManager?> TryImportAsync(HDFingerprint? masterFingerprint, string walletFilePath, bool enableCoinjoin, IProgress<BitcoinAddress>? addressToConfirm, CancellationToken cancellationToken)
	{
		using var client = _newClient();
		KruxClient.KruxInfo info;
		try
		{
			info = await client.GetInfoAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (HardwareWalletTransportNotFoundException)
		{
			return null; // no bridge, so no Krux; another vendor's device may be the one connected
		}

		if (masterFingerprint is { } expected && expected != info.Fingerprint)
		{
			throw new HardwareWalletException($"The bridge serves fingerprint {info.Fingerprint}, expected {expected}.");
		}

		var segwitAccountKeyPath = KeyManager.GetAccountKeyPath(_network, ScriptPubKeyType.Segwit);
		var taprootAccountKeyPath = KeyManager.GetAccountKeyPath(_network, ScriptPubKeyType.TaprootBIP86);
		var (segwitFingerprint, segwitExtPubKey) = await client.GetXpubAsync(segwitAccountKeyPath, cancellationToken).ConfigureAwait(false);
		var (taprootFingerprint, taprootExtPubKey) = await client.GetXpubAsync(taprootAccountKeyPath, cancellationToken).ConfigureAwait(false);
		if (segwitFingerprint != info.Fingerprint || taprootFingerprint != info.Fingerprint)
		{
			throw new HardwareWalletException("The accounts the bridge handed out belong to another seed than the authorized session. Nothing was imported.");
		}

		var keyManager = KeyManager.CreateNewHardwareWalletWatchOnly(info.Fingerprint, segwitExtPubKey, taprootExtPubKey, null, null, _network, walletFilePath, taprootAccountKeyPath);
		keyManager.CoinJoinVendor = HardwareCoinJoinVendor.Krux;
		keyManager.CoinJoinDisabled = !enableCoinjoin;
		keyManager.DefaultReceiveScriptType = ScriptPubKeyType.TaprootBIP86; // what the device signs in a round
		keyManager.SetIcon(WalletType.Hardware);
		keyManager.ToFile();

		addressToConfirm?.Report(taprootExtPubKey.Derive(0).Derive(0).PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, _network));
		return keyManager;
	}

	public async Task<IKeyChain> AuthorizeCoinJoinAsync(
		KeyManager keyManager,
		IKeyChain? existingKeyChain,
		string coordinatorIdentifier,
		int maxRounds,
		FeeRate maxMiningFeeRate,
		CancellationToken cancellationToken)
	{
		if (existingKeyChain is { NeedsReauthorization: false } and KruxKeyChain connected)
		{
			connected.MaxMiningFeeRate = maxMiningFeeRate;
			return connected;
		}

		// Either there is no session yet, or the one we had has spent its rounds. Reconnecting re-reads the
		// budget, so a session the user has since re-approved on the device is picked up; one they have not
		// fails below with the reason. Returning the spent chain instead would leave the caller asking for
		// authorization it already has, forever.
		(existingKeyChain as IDisposable)?.Dispose();

		var client = _newClient();
		try
		{
			var info = await client.GetInfoAsync(cancellationToken).ConfigureAwait(false);
			if (keyManager.MasterFingerprint is not { } expectedFingerprint || info.Fingerprint != expectedFingerprint)
			{
				throw new HardwareWalletException($"The bridge serves fingerprint {info.Fingerprint}, expected {keyManager.MasterFingerprint}.");
			}

			if (info.Authorized is false)
			{
				throw new HardwareWalletException("The device has not approved a signing session. Start kruxd and approve the budget on the device.");
			}

			if (info.MaxRounds > 0 && info.RoundsUsed >= info.MaxRounds)
			{
				throw new HardwareWalletException("The signing session has exhausted its round budget. Re-approve the session on the device.");
			}

			// What the device's own session has left, so a coinjoin is never started on a spent one. The
			// wallet's fee-rate cap is Wasabi's to enforce: the device budgets in satoshis, not per vByte.
			var roundsRemaining = info.MaxRounds > 0 ? info.MaxRounds - info.RoundsUsed : int.MaxValue;
			return new KruxKeyChain(client, keyManager, roundsRemaining, info.ScriptTypes) { MaxMiningFeeRate = maxMiningFeeRate };
		}
		catch
		{
			client.Dispose();
			throw;
		}
	}

	public void Dispose()
	{
	}
}
