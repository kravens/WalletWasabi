using NBitcoin;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Hwi;

/// <summary>
/// Pins the NBitcoin behavior ColdcardKeyChain.SignOnDevice depends on: a multi-party coinjoin PSBT has
/// foreign inputs that carry only a witness UTXO and no signature, so a full <c>Finalize()</c> throws on
/// them — the key chain must use <c>TryFinalize</c>, which still finalizes our signed inputs.
/// </summary>
public class ColdcardPsbtFinalizeTests
{
	[Fact]
	public void TryFinalizeFinalizesOurInputsDespiteForeignOnes()
	{
		var network = Network.RegTest;
		using var ourKey = new Key();
		using var foreignKey = new Key();
		using var outputKey1 = new Key();
		using var outputKey2 = new Key();

		var ourFunding = network.CreateTransaction();
		ourFunding.Outputs.Add(Money.Coins(1m), ourKey.PubKey.GetScriptPubKey(ScriptPubKeyType.Segwit));
		var foreignFunding = network.CreateTransaction();
		foreignFunding.Outputs.Add(Money.Coins(2m), foreignKey.PubKey.GetScriptPubKey(ScriptPubKeyType.Segwit));

		var coinjoin = network.CreateTransaction();
		coinjoin.Inputs.Add(new OutPoint(ourFunding, 0));
		coinjoin.Inputs.Add(new OutPoint(foreignFunding, 0));
		coinjoin.Outputs.Add(Money.Coins(0.999m), outputKey1.PubKey.GetScriptPubKey(ScriptPubKeyType.Segwit));
		coinjoin.Outputs.Add(Money.Coins(1.999m), outputKey2.PubKey.GetScriptPubKey(ScriptPubKeyType.Segwit));

		var psbt = PSBT.FromTransaction(coinjoin, network);
		psbt.Inputs[0].WitnessUtxo = ourFunding.Outputs[0];
		psbt.Inputs[1].WitnessUtxo = foreignFunding.Outputs[0];

		// The device signs only our input (stxn with the do-not-finalize flag returns partial sigs).
		psbt.Inputs[0].Sign(ourKey);

		// Full finalization must fail on the unsigned foreign input...
		Assert.Throws<PSBTException>(() => psbt.Clone().Finalize());

		// ...while TryFinalize still produces the witness for our input.
		Assert.False(psbt.TryFinalize(out _));
		Assert.NotNull(psbt.Inputs[0].FinalScriptWitness);
		Assert.Null(psbt.Inputs[1].FinalScriptWitness);
	}
}
