using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Hwi;
using WalletWasabi.Hwi.Coldcard;
using WalletWasabi.Hwi.Models;
using WalletWasabi.Hwi.Trezor;
using WalletWasabi.Logging;

namespace WalletWasabi.Helpers;

public static class HardwareWalletOperationHelpers
{
	/// <summary>Whether the device model can act as a remote signer for coinjoins (SLIP-25).</summary>
	public static bool SupportsCoinJoin(this HwiEnumerateEntry device) => device.Model.SupportsCoinJoin();

	/// <param name="enableCoinjoin">When true, also fetches the SLIP-25 coinjoin account so the device can sign coinjoins. Requires the Trezor Bridge and a confirmation on the device.</param>
	public static async Task<KeyManager> GenerateWalletAsync(HwiEnumerateEntry device, string walletFilePath, Network network, CancellationToken cancelToken, bool enableCoinjoin = false)
	{
		if (device.Fingerprint is null)
		{
			throw new Exception("Fingerprint cannot be null.");
		}

		var fingerPrint = (HDFingerprint)device.Fingerprint;
		var segwitAccountKeyPath = KeyManager.GetAccountKeyPath(network, ScriptPubKeyType.Segwit);

		using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
		using var genCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancelToken);

		// Trezor ONLY: this reads a SLIP-25 account over the Trezor bridge, so it must be gated on the
		// vendor and not on "can this device coinjoin". Gating it on the generic predicate sent Coldcards
		// down the bridge path, where they failed with "No Trezor device found".
		if (enableCoinjoin && device.Model.VendorOf() is HardwareCoinJoinVendor.Trezor)
		{
			// Coinjoin needs the SLIP-25 account, which only the bridge can read. Read the segwit account from the
			// bridge in the same session too, so HWI and the bridge don't contend for the USB device. If the bridge
			// is unavailable the whole import fails with a clear error instead of silently dropping coinjoin.
			using var trezor = await TrezorDevice.FindAsync(fingerPrint, genCts.Token).ConfigureAwait(false);
			var segwitFromBridge = await trezor.GetSegwitAccountXpubAsync(segwitAccountKeyPath, network, genCts.Token).ConfigureAwait(false);
			var coinJoinAccountKeyPath = TrezorDevice.GetCoinJoinAccountKeyPath(network);
			var coinJoinFromBridge = await trezor.GetCoinJoinXpubAsync(coinJoinAccountKeyPath, network, genCts.Token).ConfigureAwait(false);

			return CreateCoinJoinWatchOnly(fingerPrint, segwitFromBridge, coinJoinFromBridge, coinJoinAccountKeyPath, network, walletFilePath);
		}

		var client = new HwiClient(network);
		var segwitExtPubKey = await client.GetXpubAsync(device.Model, device.Path, segwitAccountKeyPath, genCts.Token).ConfigureAwait(false);
		var keyManager = KeyManager.CreateNewHardwareWalletWatchOnly(fingerPrint, segwitExtPubKey, null, null, null, network, walletFilePath);

		// Vendors that sign from the wallet's default accounts (no SLIP-25 account to recognise them by)
		// are recorded here; the standard HWI import above already read the segwit account, and isolation
		// comes from the device-side policy. Trezor is left alone — its account shape identifies it.
		// (Segwit only for now; taproot needs edge firmware and is a follow-up.)
		var vendor = device.Model.VendorOf();
		if (enableCoinjoin && vendor is not (HardwareCoinJoinVendor.None or HardwareCoinJoinVendor.Trezor))
		{
			keyManager.CoinJoinVendor = vendor;
			keyManager.ToFile();
		}

		return keyManager;
	}

	/// <summary>
	/// Imports whichever coinjoin-capable hardware wallet is connected, without a GUI. Trezor keeps its
	/// bridge-only path (the SLIP-25 account is not reachable over HWI); every other vendor is enumerated
	/// with HWI and imported the same way the GUI does it. Fails when the connected devices are ambiguous,
	/// rather than picking one for the user.
	/// </summary>
	public static async Task<KeyManager> ImportHardwareWalletAsync(string walletFilePath, Network network, bool enableCoinjoin, CancellationToken cancelToken)
	{
		// HWI needs the USB device to itself, so release a coinjoin bridge we may have started.
		TrezorBridgeManager.StopIfOurs();

		var client = new HwiClient(network);
		var entries = (await client.EnumerateAsync(cancelToken).ConfigureAwait(false)).ToArray();
		var usable = entries.Where(e => e.Model.SupportsCoinJoin() && e.Fingerprint is not null).ToArray();

		if (usable.Length == 0)
		{
			var seen = entries.Length == 0 ? "none" : string.Join(", ", entries.Select(e => e.Model.ToString()));
			throw new InvalidOperationException(
				$"No coinjoin-capable hardware wallet found. Connect and unlock the device. Devices seen: {seen}.");
		}
		if (usable.Length > 1)
		{
			throw new InvalidOperationException(
				"More than one coinjoin-capable hardware wallet is connected: "
				+ string.Join(", ", usable.Select(e => $"{e.Model} ({e.Fingerprint})"))
				+ ". Leave only the one to import connected.");
		}

		var device = usable[0];
		if (device.Model.VendorOf() is HardwareCoinJoinVendor.Trezor)
		{
			return await ImportTrezorWalletAsync(walletFilePath, network, enableCoinjoin, cancelToken).ConfigureAwait(false);
		}

		var keyManager = await GenerateWalletAsync(device, walletFilePath, network, cancelToken, enableCoinjoin).ConfigureAwait(false);
		keyManager.SetIcon(device.Model.ToString());
		return keyManager;
	}

	/// <summary>
	/// Imports the connected Trezor as a watch-only wallet using only the Trezor Bridge, so it works on a
	/// headless daemon without HWI. With <paramref name="enableCoinjoin"/> the SLIP-25 coinjoin account is
	/// read too, which the device asks to confirm with the coinjoin path unlock.
	/// </summary>
	public static async Task<KeyManager> ImportTrezorWalletAsync(string walletFilePath, Network network, bool enableCoinjoin, CancellationToken cancelToken)
	{
		using var trezor = await TrezorDevice.FindAsync(null, cancelToken).ConfigureAwait(false);
		var fingerprint = await trezor.GetMasterFingerprintAsync(cancelToken).ConfigureAwait(false);
		var segwitAccountKeyPath = KeyManager.GetAccountKeyPath(network, ScriptPubKeyType.Segwit);
		var segwitExtPubKey = await trezor.GetSegwitAccountXpubAsync(segwitAccountKeyPath, network, cancelToken).ConfigureAwait(false);

		KeyManager keyManager;
		if (enableCoinjoin)
		{
			var coinJoinAccountKeyPath = TrezorDevice.GetCoinJoinAccountKeyPath(network);
			var coinJoinExtPubKey = await trezor.GetCoinJoinXpubAsync(coinJoinAccountKeyPath, network, cancelToken).ConfigureAwait(false);
			keyManager = CreateCoinJoinWatchOnly(fingerprint, segwitExtPubKey, coinJoinExtPubKey, coinJoinAccountKeyPath, network, walletFilePath);
		}
		else
		{
			keyManager = KeyManager.CreateNewHardwareWalletWatchOnly(fingerprint, segwitExtPubKey, null, null, null, network, walletFilePath);
		}

		keyManager.SetIcon(Wallets.WalletType.Trezor);
		return keyManager;
	}

	private static KeyManager CreateCoinJoinWatchOnly(HDFingerprint fingerprint, ExtPubKey segwitExtPubKey, ExtPubKey coinJoinExtPubKey, KeyPath coinJoinAccountKeyPath, Network network, string walletFilePath)
	{
		var keyManager = KeyManager.CreateNewHardwareWalletWatchOnly(fingerprint, segwitExtPubKey, coinJoinExtPubKey, null, null, network, walletFilePath, coinJoinAccountKeyPath);

		// Only coins of the SLIP-25 account can join rounds, so hand out its addresses by default;
		// segwit receive stays available for deposits that should not be coinjoined.
		keyManager.DefaultReceiveScriptType = ScriptPubKeyType.TaprootBIP86;
		return keyManager;
	}

	/// <summary>
	/// Enables coinjoin on an already imported hardware watch-only wallet. The vendor is detected by
	/// enumerating over HWI and matching the wallet's fingerprint: a Coldcard just gets marked (it uses the
	/// default segwit account, already imported); a Trezor gets its SLIP-25 coinjoin account added via the
	/// bridge (needs a device confirmation). No-op if already enabled. Throws with a clear message when the
	/// device is unavailable, so the caller can tell the user rather than silently doing nothing.
	/// </summary>
	public static async Task EnableCoinJoinAsync(KeyManager keyManager, Network network, CancellationToken cancelToken)
	{
		if (!keyManager.IsHardwareWallet)
		{
			throw new InvalidOperationException("Only a hardware wallet can have coinjoin enabled.");
		}
		if (keyManager.IsHardwareCoinJoinWallet())
		{
			return;
		}

		// Which device is this? HWI needs the USB device to itself, so release any coinjoin bridge first.
		TrezorBridgeManager.StopIfOurs();
		var client = new HwiClient(network);
		using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancelToken);
		var entry = (await client.EnumerateAsync(linked.Token).ConfigureAwait(false))
			.FirstOrDefault(e => e.Fingerprint == keyManager.MasterFingerprint);
		if (entry is null)
		{
			throw new InvalidOperationException("Hardware wallet not found. Connect and unlock the device, then try again.");
		}

		var vendor = entry.Model.VendorOf();
		if (vendor is HardwareCoinJoinVendor.None)
		{
			throw new InvalidOperationException($"A {entry.Model} cannot act as a coinjoin remote signer.");
		}

		if (vendor is not HardwareCoinJoinVendor.Trezor)
		{
			// Signs from the default accounts (already imported) under a device-side policy; just record it.
			keyManager.CoinJoinVendor = vendor;
			keyManager.ToFile();
			return;
		}

		// Trezor: the SLIP-25 account is only reachable through the bridge (needs a device confirmation).
		using var trezor = await TrezorDevice.FindAsync(keyManager.MasterFingerprint, cancelToken).ConfigureAwait(false);
		var coinJoinAccountKeyPath = TrezorDevice.GetCoinJoinAccountKeyPath(network);
		var coinJoinExtPubKey = await trezor.GetCoinJoinXpubAsync(coinJoinAccountKeyPath, network, cancelToken).ConfigureAwait(false);

		keyManager.SetCoinJoinAccount(coinJoinAccountKeyPath, coinJoinExtPubKey);
	}

	public static async Task InitHardwareWalletAsync(HwiEnumerateEntry device, Network network, CancellationToken cancelToken)
	{
		var client = new HwiClient(network);
		using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(21));
		using var initCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancelToken);

		// Trezor T doesn't require interactive mode.
		var interactiveMode = !(device.Model == HardwareWalletModels.Trezor_T || device.Model == HardwareWalletModels.Trezor_T_Simulator);

		try
		{
			await client.SetupAsync(device.Model, device.Path, interactiveMode, initCts.Token).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			Logger.LogError(ex);
		}
	}

	public static async Task<HwiEnumerateEntry[]> DetectAsync(Network network, CancellationToken cancelToken)
	{
		// HWI needs exclusive USB access, which a coinjoin bridge we started would hold. Release it so
		// detection works; a loaded coinjoin wallet restarts the bridge the next time it needs to sign.
		TrezorBridgeManager.StopIfOurs();

		var client = new HwiClient(network);
		using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancelToken);

		var detectedHardwareWallets = (await client.EnumerateAsync(timeoutCts.Token).ConfigureAwait(false)).ToArray();

		cancelToken.ThrowIfCancellationRequested();

		return detectedHardwareWallets;
	}
}
