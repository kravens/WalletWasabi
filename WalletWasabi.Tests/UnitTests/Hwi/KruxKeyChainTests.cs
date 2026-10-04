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
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Hwi;

/// <summary>The coinjoin a policy signer is handed, and what comes back from it, without a device.</summary>
public class KruxKeyChainTests
{
	[Fact]
	public void OurInputsAndOutputsCarryTheirDerivationForeignOnesNothing()
	{
		var (round, coins, keyManager) = TestKeyManagers.Round();

		var psbt = RemoteSignerPsbt.Build(round, keyManager);

		Assert.All(psbt.Inputs, input => Assert.Equal(coins[(int)input.Index].TxOut, input.WitnessUtxo));

		var taprootPath = Assert.Single(psbt.Inputs[0].HDTaprootKeyPaths).Value;
		Assert.Equal(keyManager.MasterFingerprint, taprootPath.RootedKeyPath.MasterFingerprint);
		Assert.Equal(new KeyPath("86'/0'/0'/0/0"), taprootPath.RootedKeyPath.KeyPath);
		Assert.Empty(psbt.Inputs[0].HDKeyPaths);

		var segwitPath = Assert.Single(psbt.Inputs[1].HDKeyPaths).Value;
		Assert.Equal(new KeyPath("84'/0'/0'/0/0"), segwitPath.KeyPath);
		Assert.Empty(psbt.Inputs[1].HDTaprootKeyPaths);

		Assert.Empty(psbt.Inputs[2].HDKeyPaths);
		Assert.Empty(psbt.Inputs[2].HDTaprootKeyPaths);

		Assert.Single(psbt.Outputs[0].HDTaprootKeyPaths);
		Assert.Empty(psbt.Outputs[1].HDTaprootKeyPaths);
		Assert.Empty(psbt.Outputs[1].HDKeyPaths);
	}

	[Fact]
	public void WitnessesComeBackForTaprootAndSegwitInputsAlike()
	{
		var (round, coins, keyManager) = TestKeyManagers.Round();
		var signed = RemoteSignerPsbt.Build(round, keyManager);
		using var key = new Key();
		signed.Inputs[0].TaprootKeySignature = key.SignTaprootKeySpend(uint256.One, null, TaprootSigHash.Default);
		signed.Inputs[1].PartialSigs.Add(key.PubKey, new TransactionSignature(key.Sign(uint256.One), SigHash.All));

		var witnesses = RemoteSignerPsbt.Witnesses(signed);

		Assert.Equal(2, witnesses.Count);
		Assert.Single(witnesses[coins[0].Outpoint].Pushes);
		Assert.Equal(2, witnesses[coins[1].Outpoint].Pushes.Count());
		Assert.False(witnesses.ContainsKey(coins[2].Outpoint));
	}

	[Fact]
	public void ADeviceThatReturnsNoSignatureIsAnError()
	{
		var (round, _, keyManager) = TestKeyManagers.Round();

		Assert.Throws<InvalidOperationException>(() => RemoteSignerPsbt.Witnesses(RemoteSignerPsbt.Build(round, keyManager)));
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
		Assert.Null(taprootOnly.MaxMiningFeeRate);
	}

	/// <summary>The chain owns and disposes the client it is given.</summary>
	internal static KruxKeyChain Chain(FakeKruxd kruxd, KeyManager keyManager, int roundsRemaining = int.MaxValue, IReadOnlyCollection<ScriptType>? scriptTypes = null)
	{
#pragma warning disable CA2000 // Dispose objects before losing scope - owned by the key chain.
		return new KruxKeyChain(new KruxClient("http://127.0.0.1:1", kruxd), keyManager, roundsRemaining, scriptTypes);
#pragma warning restore CA2000
	}
}
