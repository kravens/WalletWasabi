using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using NBitcoin;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Crypto;
using WalletWasabi.Hwi.Krux;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.WabiSabi.Models.MultipartyTransaction;
using WalletWasabi.Wallets;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Hwi;

/// <summary>What a Krux session is asked for, and when it is spent, without a device.</summary>
public class KruxKeyChainTests
{
	[Fact]
	public void ADeviceThatSignsNothingRefusedTheRound()
	{
		// Retrying builds the same round, so this stops the coinjoin as a refusal rather than being retried.
		var (round, coins, keyManager) = TestKeyManagers.Round();
		using var kruxd = new FakeKruxd();
		kruxd.Replies["/sign"] = request => (System.Net.HttpStatusCode.OK, $$"""{"psbt":"{{request.RootElement.GetProperty("psbt").GetString()}}"}""");
		using var keyChain = Chain(kruxd, keyManager);

		Assert.Throws<HardwareWalletException>(() => keyChain.Sign(round, coins[0]));
	}

	[Fact]
	public void OneSignedRoundSpendsOneRoundOfTheSessionAndIsSignedOnce()
	{
		var (round, coins, keyManager) = TestKeyManagers.Round();
		using var kruxd = new FakeKruxd();
		kruxd.Replies["/sign"] = request =>
		{
			// The device signs our taproot input and hands the PSBT back.
			var psbt = PSBT.Parse(request.RootElement.GetProperty("psbt").GetString()!, Network.Main);
			using var signer = new Key();
			psbt.Inputs[0].TaprootKeySignature = signer.SignTaprootKeySpend(uint256.One, null, TaprootSigHash.Default);
			return (System.Net.HttpStatusCode.OK, $$"""{"psbt":"{{psbt.ToBase64()}}"}""");
		};
		using var keyChain = Chain(kruxd, keyManager, roundsRemaining: 1, [ScriptType.Taproot]);

		Assert.False(keyChain.NeedsAuthorization);
		var signed = keyChain.Sign(round, coins[0]);
		keyChain.Sign(round, coins[0]);

		Assert.Single(kruxd.Requests);
		Assert.Single(signed.Inputs[0].WitScript.Pushes);
		Assert.True(keyChain.NeedsAuthorization);
	}

	[Fact]
	public void TheChainSaysWhatTheBridgeReportedAndThatSigningTakesTime()
	{
		var keyManager = TestKeyManagers.PolicySignerWallet();
		using var kruxd = new FakeKruxd();
		using var taprootOnly = Chain(kruxd, keyManager, scriptTypes: [ScriptType.Taproot]);
		using var both = Chain(kruxd, keyManager);

		Assert.True(taprootOnly.CanSign(ScriptType.Taproot));
		Assert.False(taprootOnly.CanSign(ScriptType.P2WPKH));
		Assert.True(both.CanSign(ScriptType.P2WPKH));
		Assert.True(taprootOnly.SigningTakesTime);
		Assert.Equal(new FeeRate(keyManager.CoinJoinDeviceMaxMiningFeeRate), taprootOnly.MaxMiningFeeRate); // the wallet's, read live
	}

	/// <summary>The chain owns and disposes the client it is given.</summary>
	internal static KruxKeyChain Chain(FakeKruxd kruxd, KeyManager keyManager, int roundsRemaining = int.MaxValue, IReadOnlyCollection<ScriptType>? scriptTypes = null)
	{
#pragma warning disable CA2000 // Dispose objects before losing scope - owned by the key chain.
		return new KruxKeyChain(new KruxClient("http://127.0.0.1:1", kruxd), keyManager, roundsRemaining, scriptTypes);
#pragma warning restore CA2000
	}
}
