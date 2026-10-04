using System.Collections.Immutable;
using System.Linq;
using NBitcoin;
using WalletWasabi.Crypto;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.WabiSabi.Models.MultipartyTransaction;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Hwi;

/// <summary>What a Coldcard is handed to sign a round from.</summary>
public class ColdcardKeyChainTests
{
	[Fact]
	public void ATaprootInputOfOursBringsEveryWitnessUtxoAlong()
	{
		var (round, coins, keyManager) = TestKeyManagers.Round();

		var (psbt, ours) = ColdcardKeyChain.CreatePsbt(round, keyManager);

		Assert.Equal(coins.Take(2).Select(coin => coin.Outpoint).ToHashSet(), ours);
		Assert.All(psbt.Inputs, input => Assert.NotNull(input.WitnessUtxo)); // BIP-341 signs them all
	}

	[Fact]
	public void ASegwitOnlyRoundLeavesForeignInputsBare()
	{
		var (_, coins, keyManager) = TestKeyManagers.Round();
		Coin[] segwitAndForeign = [coins[1], coins[2]];
		var transaction = Transaction.Create(Network.Main);
		foreach (var coin in segwitAndForeign)
		{
			transaction.Inputs.Add(coin.Outpoint);
		}
		transaction.Outputs.Add(Money.Coins(1.99m), coins[2].ScriptPubKey);
		var round = new TransactionWithPrecomputedData(transaction, transaction.PrecomputeTransactionData(segwitAndForeign), ImmutableDictionary<OutPoint, OwnershipProof>.Empty);

		var (psbt, _) = ColdcardKeyChain.CreatePsbt(round, keyManager);

		Assert.NotNull(psbt.Inputs[0].WitnessUtxo);
		Assert.Null(psbt.Inputs[1].WitnessUtxo);
	}
}
