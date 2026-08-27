using NBitcoin;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Hwi;
using WalletWasabi.Hwi.Krux;
using WalletWasabi.WabiSabi.Client;

namespace WalletWasabi.Wallets.Backends;

/// <summary>
/// Krux, reached through the external kruxd bridge. Nothing is authorized from here: the user approves the
/// signing session on the device itself, which by design the host cannot alter. All this does is confirm the
/// bridge is serving this wallet's device and that the session it approved still has rounds left.
/// </summary>
internal class KruxBackend : IHardwareWalletBackend
{
	public HardwareCoinJoinVendor Vendor => HardwareCoinJoinVendor.Krux;

	public async Task<IKeyChain> AuthorizeCoinJoinAsync(
		KeyManager keyManager,
		IKeyChain? existingKeyChain,
		string coordinatorIdentifier,
		int maxRounds,
		FeeRate maxMiningFeeRate,
		CancellationToken cancellationToken)
	{
		if (existingKeyChain is KruxKeyChain connected)
		{
			return connected;
		}

		var client = new KruxClient();
		try
		{
			var info = await client.GetInfoAsync(cancellationToken).ConfigureAwait(false);
			if (keyManager.MasterFingerprint is not { } expectedFingerprint || info.Fingerprint != expectedFingerprint)
			{
				throw new HardwareWalletException($"The Krux serves fingerprint {info.Fingerprint}, expected {keyManager.MasterFingerprint}.");
			}

			if (info.MaxRounds > 0 && info.RoundsUsed >= info.MaxRounds)
			{
				throw new HardwareWalletException("The Krux signing session has exhausted its round budget. Re-approve the session on the device.");
			}

			// What the device's own session has left, so a coinjoin is never started on a spent one.
			var roundsRemaining = info.MaxRounds > 0 ? (int)(info.MaxRounds - info.RoundsUsed) : int.MaxValue;
			return new KruxKeyChain(client, keyManager, roundsRemaining);
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
