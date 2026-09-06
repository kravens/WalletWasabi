using NBitcoin;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Hwi;
using WalletWasabi.WabiSabi.Client;

namespace WalletWasabi.Wallets;

/// <summary>
/// What one hardware vendor can do for a coinjoin wallet. Vendors share nothing at the device level - one
/// speaks protobuf over an HTTP bridge, another an encrypted HID protocol, another talks to a daemon - so
/// what is shared here is what an operation <i>means</i>, not how it travels.
///
/// Only <see cref="ImportAsync"/> and <see cref="AuthorizeCoinJoinAsync"/> have to be written: everything
/// else describes something a vendor may simply not have, and defaults to "I do not do that", which lets
/// <see cref="HardwareWalletService"/> fall back to HWI. Adding a vendor is a value in
/// <see cref="HardwareCoinJoinVendor"/>, an entry in <c>VendorOf</c>, one class here, and an
/// <see cref="IKeyChain"/>.
/// </summary>
/// <summary>What a device reports about the policy it is running, in its own words plus the hash that
/// identifies it.</summary>
public record DevicePolicyReport(string Summary, string PolicyHash);

internal interface IHardwareWalletBackend : IDisposable
{
	HardwareCoinJoinVendor Vendor { get; }

	/// <summary>
	/// Reads the wallet's accounts over the vendor's own transport, for a vendor that keeps something HWI
	/// cannot see. Returns null when HWI's own import is enough, which is the usual case: a vendor that
	/// signs from the wallet's ordinary accounts has nothing extra to read.
	/// </summary>
	/// <param name="masterFingerprint">Which device to use, or null to take the one that is connected.</param>
	/// <param name="addressToConfirm">
	/// Told each address the device is about to show, so the caller can put it next to the device for the
	/// user to compare. A transport that is not trusted - an unauthenticated local bridge - has every account
	/// it hands out proven this way before anything is written.
	/// </param>
	Task<KeyManager?> TryImportAsync(HDFingerprint? masterFingerprint, string walletFilePath, bool enableCoinjoin, IProgress<BitcoinAddress>? addressToConfirm, CancellationToken cancellationToken) =>
		Task.FromResult<KeyManager?>(null);

	/// <summary>Asks the device to authorize a batch of coinjoin rounds and returns the key chain that signs them.</summary>
	/// <param name="existingKeyChain">The wallet's current key chain, reused when it already holds the device.</param>
	Task<IKeyChain> AuthorizeCoinJoinAsync(
		KeyManager keyManager,
		IKeyChain? existingKeyChain,
		string coordinatorIdentifier,
		int maxRounds,
		FeeRate maxMiningFeeRate,
		CancellationToken cancellationToken);

	/// <summary>
	/// Turns an already imported watch-only wallet into one this vendor can coinjoin with. An account read
	/// for it is proven on the device the way an import is, see <see cref="TryImportAsync"/>.
	/// </summary>
	Task EnableCoinJoinAsync(KeyManager keyManager, IProgress<BitcoinAddress>? addressToConfirm, CancellationToken cancellationToken) => Task.CompletedTask;

	/// <summary>Signs over the vendor's transport, or returns null to let HWI do it.</summary>
	Task<PSBT?> TrySignTransactionAsync(KeyManager keyManager, PSBT psbt, SmartTransaction transaction, CancellationToken cancellationToken) =>
		Task.FromResult<PSBT?>(null);

	/// <summary>Shows and verifies an address over the vendor's transport; false means HWI should do it.</summary>
	Task<bool> TryDisplayAddressAsync(KeyManager keyManager, KeyPath fullKeyPath, BitcoinAddress expectedAddress, CancellationToken cancellationToken) =>
		Task.FromResult(false);

	/// <summary>
	/// What the device says it is currently enforcing, read back from the device itself rather than from
	/// what we believe we sent it - which is the only way a user can check the limits are the agreed ones.
	/// Null when the vendor has no policy to describe.
	/// </summary>
	Task<DevicePolicyReport?> GetDevicePolicyAsync(IKeyChain keyChain, CancellationToken cancellationToken) =>
		Task.FromResult<DevicePolicyReport?>(null);

	/// <summary>Whether a device of this vendor can currently be reached at all.</summary>
	Task<bool> IsTransportAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

	/// <summary>Brings up whatever this vendor needs before its device can be used.</summary>
	Task EnsureReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	/// <summary>
	/// Hands the device back, so HWI - which needs the device for itself - can use it. Returns whether
	/// anything was actually given up, so the caller knows whether to put it back afterwards.
	/// </summary>
	bool Release() => false;

	/// <summary>Whether borrowing the device for an HWI operation would disturb this vendor's transport.</summary>
	bool SharesTransportWith(KeyManager keyManager) => false;
}
