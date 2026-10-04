using WalletWasabi.Hwi;
using WalletWasabi.Hwi.Models;
using WalletWasabi.WabiSabi.Client;

namespace WalletWasabi.Wallets;

/// <summary>
/// What one hardware vendor can do for a coinjoin wallet. Vendors share nothing at the device level - one
/// speaks protobuf over an HTTP bridge, another an encrypted HID protocol, another talks to a daemon - so
/// what is shared here is what an operation <i>means</i>, not how it travels.
///
/// Only authorizing a batch of rounds, and letting go of what that holds, have to be written: everything else
/// describes something a vendor may simply not have, and defaults to "I do not do that", which lets
/// <see cref="HardwareWalletService"/> fall back to HWI. Adding a vendor is a value in
/// <see cref="HardwareCoinJoinVendor"/>, an entry in <c>VendorOf</c>, one class here, and an <see cref="IKeyChain"/>.
/// </summary>
internal interface IHardwareWalletBackend : IDisposable
{
	HardwareCoinJoinVendor Vendor { get; }

	/// <summary>A device HWI cannot see, found over the vendor's own transport; null when none answers.</summary>
	Task<HwiEnumerateEntry?> TryDetectAsync(CancellationToken cancellationToken) => Task.FromResult<HwiEnumerateEntry?>(null);

	/// <summary>
	/// Reads the wallet's accounts over the vendor's own transport. Returns null when HWI's own import is
	/// enough: a vendor that signs from the wallet's ordinary accounts has nothing extra to read.
	/// </summary>
	/// <param name="masterFingerprint">Which device to use, or null to take the one that is connected.</param>
	/// <param name="addressToConfirm">Told each address the device is about to show, so the caller can put it next to the device for the user to compare; an untrusted transport has every account it hands out proven this way before anything is written.</param>
	Task<KeyManager?> TryImportAsync(HDFingerprint? masterFingerprint, string walletFilePath, bool enableCoinjoin, IProgress<BitcoinAddress>? addressToConfirm, CancellationToken cancellationToken) =>
		Task.FromResult<KeyManager?>(null);

	/// <summary>
	/// Asks the device to authorize the wallet's coinjoin limits and returns the key chain that signs the rounds.
	/// The backend keeps that key chain, and the device session it holds, until <see cref="ReleaseFor"/>.
	/// </summary>
	Task<IKeyChain> AuthorizeCoinJoinAsync(KeyManager keyManager, string coordinatorIdentifier, CancellationToken cancellationToken);

	/// <summary>Whether a device of this vendor can currently be reached at all.</summary>
	Task<bool> IsTransportAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

	/// <summary>Drops the wallet's key chain and the device session it holds, for a wallet that no longer needs the device; a transport that serves other wallets stays.</summary>
	void ReleaseFor(KeyManager keyManager);

	/// <summary>Most vendors start nothing of their own: what they hold, they hold per wallet, and <see cref="ReleaseFor"/> lets go of it.</summary>
	void IDisposable.Dispose()
	{
	}
}
