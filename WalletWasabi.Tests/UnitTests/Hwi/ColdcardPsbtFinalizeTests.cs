using System.Collections.Generic;
using System.Linq;
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

	[Fact]
	public void TryFinalizeFinalizesEveryOneOfOurInputs()
	{
		// A wallet contributes one input per round only while its coins are still "red" — the
		// selector isolates those from each other. Once coins reach semi-private, several of ours
		// land in the same round, and SignOnDevice must come back with a witness for each. The
		// witness dictionary it builds is keyed by outpoint, so this also pins that our inputs stay
		// distinguishable from the foreign ones when they are interleaved.
		var network = Network.RegTest;
		var ourKeys = new[] { new Key(), new Key(), new Key() };
		var foreignKeys = new[] { new Key(), new Key() };

		var coinjoin = network.CreateTransaction();
		var fundings = new List<Transaction>();
		var ourOutpoints = new List<OutPoint>();

		// Interleave ours and theirs, so a bug that assumes a contiguous block of our inputs fails.
		var owners = new[] { ourKeys[0], foreignKeys[0], ourKeys[1], foreignKeys[1], ourKeys[2] };
		foreach (var (key, index) in owners.Select((k, i) => (k, i)))
		{
			var funding = network.CreateTransaction();
			funding.Outputs.Add(Money.Coins(1m), key.PubKey.GetScriptPubKey(ScriptPubKeyType.Segwit));
			fundings.Add(funding);
			coinjoin.Inputs.Add(new OutPoint(funding, 0));
			if (ourKeys.Contains(key))
			{
				ourOutpoints.Add(new OutPoint(funding, 0));
			}
		}

		using var outputKey = new Key();
		coinjoin.Outputs.Add(Money.Coins(4.99m), outputKey.PubKey.GetScriptPubKey(ScriptPubKeyType.Segwit));

		var psbt = PSBT.FromTransaction(coinjoin, network);
		for (int i = 0; i < psbt.Inputs.Count; i++)
		{
			psbt.Inputs[i].WitnessUtxo = fundings[i].Outputs[0];
		}

		// What the device returns: partial sigs on our three inputs, nothing on the foreign two.
		foreach (var key in ourKeys)
		{
			foreach (var input in psbt.Inputs)
			{
				if (input.WitnessUtxo?.ScriptPubKey == key.PubKey.GetScriptPubKey(ScriptPubKeyType.Segwit))
				{
					input.Sign(key);
				}
			}
		}

		Assert.False(psbt.TryFinalize(out _));

		var finalized = psbt.Inputs.Where(x => x.FinalScriptWitness is not null).Select(x => x.PrevOut).ToHashSet();
		Assert.Equal(ourOutpoints.ToHashSet(), finalized);

		foreach (var key in ourKeys)
		{
			key.Dispose();
		}
		foreach (var key in foreignKeys)
		{
			key.Dispose();
		}
	}
}
