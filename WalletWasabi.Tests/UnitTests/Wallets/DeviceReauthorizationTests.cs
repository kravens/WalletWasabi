using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Crypto;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.WabiSabi.Models.MultipartyTransaction;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Wallets;

/// <summary>
/// The capability members of <see cref="IKeyChain"/> are default interface implementations, so a key chain
/// that declares one under the wrong name still compiles and is simply never called - which is how a signer
/// that needs the whole signing phase quietly went back to being scheduled at random. These pin the members
/// through the interface, which is the only way the coinjoin flow ever reads them.
/// </summary>
public class DeviceReauthorizationTests
{
	private class SpentKeyChain : IKeyChain
	{
		public bool NeedsReauthorization => true;

		public bool SigningTakesTime => true;

		public int? MinRoundInputs => 20;

		public bool CanSign(ScriptType scriptType) => scriptType == ScriptType.P2WPKH;

		public OwnershipProof GetOwnershipProof(IDestination destination, CoinJoinInputCommitmentData committedData) =>
			throw new NotSupportedException();

		public Transaction Sign(TransactionWithPrecomputedData unsignedCoinJoin, Coin coin) =>
			throw new NotSupportedException();
	}

	private class PlainKeyChain : IKeyChain
	{
		public OwnershipProof GetOwnershipProof(IDestination destination, CoinJoinInputCommitmentData committedData) =>
			throw new NotSupportedException();

		public Transaction Sign(TransactionWithPrecomputedData unsignedCoinJoin, Coin coin) =>
			throw new NotSupportedException();
	}

	[Fact]
	public void CapabilitiesAreReadableThroughTheInterface()
	{
		IKeyChain spent = new SpentKeyChain();
		Assert.True(spent.NeedsReauthorization);
		Assert.True(spent.SigningTakesTime);
		Assert.Equal(20, spent.MinRoundInputs);
		Assert.False(spent.CanSign(ScriptType.Taproot));
		Assert.True(spent.CanSign(ScriptType.P2WPKH));
	}

	[Fact]
	public void ASignerWithNothingToSayKeepsTheOldBehaviour()
	{
		// A software key chain must not accidentally opt into any of the device-only rules.
		IKeyChain plain = new PlainKeyChain();
		Assert.False(plain.NeedsReauthorization);
		Assert.False(plain.SigningTakesTime);
		Assert.Null(plain.MinRoundInputs);
		Assert.True(plain.CanSign(ScriptType.Taproot));
	}

	[Fact]
	public void AnExhaustedSignerStillNeedsAuthorization()
	{
		// The pattern CoinJoinManager.NeedsDeviceAuthorization uses, pinned so it keeps matching the member
		// on the interface rather than one that happens to exist on a concrete key chain.
		IKeyChain? none = null;
		IKeyChain spent = new SpentKeyChain();
		IKeyChain live = new PlainKeyChain();

		Assert.True(none is null or { NeedsReauthorization: true });
		Assert.True(spent is null or { NeedsReauthorization: true });
		Assert.False(live is null or { NeedsReauthorization: true });
	}
}
