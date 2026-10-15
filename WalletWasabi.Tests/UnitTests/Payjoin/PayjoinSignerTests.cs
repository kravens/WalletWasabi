using NBitcoin;
using NBitcoin.Policy;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Payjoin;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Payjoin;

public class PayjoinSignerTests
{
	private const string Password = "blahblahblah";

	[Theory]
	[InlineData(ScriptPubKeyType.Segwit)]
	[InlineData(ScriptPubKeyType.TaprootBIP86)]
	public void SignsAndFinalizesOwnInput(ScriptPubKeyType scriptPubKeyType)
	{
		KeyManager keyManager = ServiceFactory.CreateKeyManager(Password, isTaprootAllowed: true);
		SmartCoin coin = BitcoinFactory.CreateSmartCoin(keyManager.GetNextReceiveKey(LabelsArray.Empty, scriptPubKeyType), 1m);
		PSBT psbt = CreatePsbt(coin);

		PayjoinSigner.Sign(psbt, Network.Main, keyManager, Password, [coin]);

		Assert.True(psbt.Inputs[0].IsFinalized());
		Transaction signed = psbt.ExtractTransaction();
		Assert.True(Network.Main.CreateTransactionBuilder().AddCoins(coin.Coin).Verify(signed, out TransactionPolicyError[] errors), string.Join("; ", errors));
	}

	[Fact]
	public void LeavesForeignInputsAlone()
	{
		KeyManager keyManager = ServiceFactory.CreateKeyManager(Password);
		SmartCoin coin = BitcoinFactory.CreateSmartCoin(keyManager.GetNextReceiveKey(LabelsArray.Empty), 1m);
		using var foreignKey = new Key();
		var foreignCoin = new Coin(BitcoinFactory.CreateOutPoint(), new TxOut(Money.Coins(1m), foreignKey.GetScriptPubKey(ScriptPubKeyType.Segwit)));
		PSBT psbt = CreatePsbt(coin, foreignCoin);

		PayjoinSigner.Sign(psbt, Network.Main, keyManager, Password, [coin]);

		Assert.True(psbt.Inputs.FindIndexedInput(coin.Outpoint)!.IsFinalized());
		Assert.False(psbt.Inputs.FindIndexedInput(foreignCoin.Outpoint)!.IsFinalized());
	}

	[Fact]
	public void RefusesProposalThatDoesNotSpendOurCoin()
	{
		KeyManager keyManager = ServiceFactory.CreateKeyManager(Password);
		SmartCoin coin = BitcoinFactory.CreateSmartCoin(keyManager.GetNextReceiveKey(LabelsArray.Empty), 1m);
		SmartCoin other = BitcoinFactory.CreateSmartCoin(keyManager.GetNextReceiveKey(LabelsArray.Empty), 1m);

		Assert.Throws<InvalidOperationException>(() => PayjoinSigner.Sign(CreatePsbt(other), Network.Main, keyManager, Password, [coin]));
	}

	private static PSBT CreatePsbt(SmartCoin coin, Coin? foreignCoin = null)
	{
		using var destination = new Key();
		Transaction tx = Network.Main.CreateTransaction();
		tx.Inputs.Add(coin.Outpoint);
		if (foreignCoin is { })
		{
			tx.Inputs.Add(foreignCoin.Outpoint);
		}
		tx.Outputs.Add(Money.Coins(0.9999m), destination.GetScriptPubKey(ScriptPubKeyType.Segwit));

		PSBT psbt = PSBT.FromTransaction(tx, Network.Main);
		psbt.AddCoins(coin.Coin);
		if (foreignCoin is { })
		{
			psbt.AddCoins(foreignCoin);
		}

		return psbt;
	}
}
