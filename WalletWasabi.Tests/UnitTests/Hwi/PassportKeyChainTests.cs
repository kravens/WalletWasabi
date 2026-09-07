using System.Linq;
using NBitcoin;
using WalletWasabi.Crypto;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.WabiSabi.Models;
using Xunit;

#pragma warning disable CA2000 // Dispose objects before losing scope - the fakes own nothing.

namespace WalletWasabi.Tests.UnitTests.Hwi;

/// <summary>What a Passport session is asked for, and when it is spent, without a device.</summary>
public class PassportKeyChainTests
{
	private static string Token => Convert.ToHexString(FakePassportDevice.SessionToken);

	private static PassportKeyChain Chain(FakePassportDevice device, int roundsRemaining = 5, DateTimeOffset? expiresAt = null) =>
		new(device, FakePassportDevice.SessionToken.ToArray(), TestKeyManagers.PolicySignerWallet(), roundsRemaining, expiresAt ?? DateTimeOffset.UtcNow.AddHours(1));

	[Fact]
	public void OneSignedRoundGoesToTheDeviceOnceUnderTheTokenAndSpendsOneRound()
	{
		var (round, coins, keyManager) = KruxKeyChainTests.Round();
		var device = new FakePassportDevice
		{
			Signer = psbt =>
			{
				using var signer = new Key();
				psbt.Inputs[0].TaprootKeySignature = signer.SignTaprootKeySpend(uint256.One, null, TaprootSigHash.Default);
				return psbt;
			},
		};
		using var keyChain = new PassportKeyChain(device, FakePassportDevice.SessionToken.ToArray(), keyManager, roundsRemaining: 1, DateTimeOffset.UtcNow.AddHours(1));

		Assert.False(keyChain.NeedsReauthorization);
		var signed = keyChain.Sign(round, coins[0]);
		keyChain.Sign(round, coins[0]);

		Assert.Equal($"sign {Token}", Assert.Single(device.Calls));
		Assert.Single(signed.Inputs[0].WitScript.Pushes);
		Assert.True(keyChain.NeedsReauthorization);
	}

	[Fact]
	public void TheOwnershipProofIsTheDevicesUnderTheTokenAndThePath()
	{
		var keyManager = TestKeyManagers.PolicySignerWallet();
		var key = keyManager.GetNextReceiveKey(new WalletWasabi.Blockchain.Analysis.Clustering.LabelsArray("a"), ScriptPubKeyType.TaprootBIP86);
		var commitment = new CoinJoinInputCommitmentData("coordinator", uint256.One);
		var device = new FakePassportDevice
		{
			Prover = (path, data) => OwnershipProof.GenerateCoinJoinInputProof(
				TestKeyManagers.MasterKey.Derive(path).PrivateKey, new OwnershipIdentifier(new Key(), key.P2Taproot), commitment, ScriptPubKeyType.TaprootBIP86).ToBytes(),
		};
		using var keyChain = Chain(device);

		var proof = keyChain.GetOwnershipProof(key.P2Taproot.GetDestinationAddress(Network.Main)!, commitment);

		Assert.Equal($"proof {Token} 86'/0'/0'/0/0", Assert.Single(device.Calls));
		Assert.True(OwnershipProof.VerifyCoinJoinInputProof(proof, key.P2Taproot, commitment));
	}

	[Fact]
	public void TheSessionExpiresByTimeAsWellAsByRounds()
	{
		using var fresh = Chain(new FakePassportDevice());
		using var spent = Chain(new FakePassportDevice(), roundsRemaining: 0);
		using var expired = Chain(new FakePassportDevice(), expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1));

		Assert.False(fresh.NeedsReauthorization);
		Assert.True(spent.NeedsReauthorization);
		Assert.True(expired.NeedsReauthorization);
		Assert.True(fresh.SigningTakesTime);
	}

	[Fact]
	public void TaprootIsSignedOnlyWhenTheDeviceAdvertisesIt()
	{
		using var taproot = Chain(new FakePassportDevice());
		using var segwitOnly = Chain(new FakePassportDevice { Capabilities = 3 });

		Assert.True(taproot.CanSign(ScriptType.Taproot));
		Assert.True(segwitOnly.CanSign(ScriptType.P2WPKH));
		Assert.False(segwitOnly.CanSign(ScriptType.Taproot));
	}

	[Fact]
	public void DisposeRevokesTheSessionWithItsTokenThenClosesTheDevice()
	{
		var device = new FakePassportDevice();
		var keyChain = Chain(device);

		keyChain.Dispose();

		Assert.Equal(FakePassportDevice.SessionToken, device.RevokedToken);
		Assert.True(device.Disposed);
	}
}
