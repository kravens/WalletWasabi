using System.Collections.Immutable;
using NBitcoin;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Crypto;
using WalletWasabi.WabiSabi.Models.MultipartyTransaction;

namespace WalletWasabi.Tests.UnitTests.Hwi;

/// <summary>Watch-only wallets as importing a device produces them, and a round to sign, from the "all all all" seed Trezor's own tests use.</summary>
internal static class TestKeyManagers
{
	public static ExtKey MasterKey => new Mnemonic("all all all all all all all all all all all all").DeriveExtKey();

	/// <summary>A segwit account, plus the SLIP-25 coinjoin account as the taproot account when asked for.</summary>
	public static KeyManager WatchOnlyHardwareWallet(bool withCoinJoinAccount)
	{
		var masterKey = MasterKey;
		var keyManager = KeyManager.CreateNewHardwareWalletWatchOnly(
			masterKey.Neuter().PubKey.GetHDFingerPrint(),
			masterKey.Derive(new KeyPath("84'/0'/0'")).Neuter(),
			null,
			null,
			null,
			Network.Main);
		if (withCoinJoinAccount)
		{
			var coinJoinAccountKeyPath = Slip25.GetCoinJoinAccountKeyPath(Network.Main);
			keyManager.SetCoinJoinAccount(coinJoinAccountKeyPath, masterKey.Derive(coinJoinAccountKeyPath).Neuter());
		}

		return keyManager;
	}

	/// <summary>The segwit and taproot accounts of a device that signs coinjoins from the wallet's ordinary accounts under its own policy (Coldcard).</summary>
	public static KeyManager PolicySignerWallet()
	{
		var masterKey = MasterKey;
		return KeyManager.CreateNewHardwareWalletWatchOnly(
			masterKey.Neuter().PubKey.GetHDFingerPrint(),
			masterKey.Derive(new KeyPath("84'/0'/0'")).Neuter(),
			masterKey.Derive(new KeyPath("86'/0'/0'")).Neuter(),
			null,
			null,
			Network.Main);
	}

	/// <summary>A round with our taproot coin, our segwit coin and a foreign coin, paying to one of ours and one foreign output.</summary>
	internal static (TransactionWithPrecomputedData Round, Coin[] Coins, KeyManager KeyManager) Round()
	{
		var keyManager = TestKeyManagers.PolicySignerWallet();
		var ourTaproot = keyManager.GetNextReceiveKey(new LabelsArray("a"), ScriptPubKeyType.TaprootBIP86);
		var ourSegwit = keyManager.GetNextReceiveKey(new LabelsArray("b"), ScriptPubKeyType.Segwit);
		var ourOutput = keyManager.GetNextReceiveKey(new LabelsArray("c"), ScriptPubKeyType.TaprootBIP86);
		using var foreignKey = new Key();
		var foreign = foreignKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);

		Coin[] coins =
		[
			new(new OutPoint(uint256.One, 0), new TxOut(Money.Coins(1m), ourTaproot.P2Taproot)),
			new(new OutPoint(uint256.One, 1), new TxOut(Money.Coins(1m), ourSegwit.P2wpkhScript)),
			new(new OutPoint(uint256.One, 2), new TxOut(Money.Coins(1m), foreign)),
		];

		var transaction = Transaction.Create(Network.Main);
		foreach (var coin in coins)
		{
			transaction.Inputs.Add(coin.Outpoint);
		}
		transaction.Outputs.Add(Money.Coins(1.99m), ourOutput.P2Taproot);
		transaction.Outputs.Add(Money.Coins(0.99m), foreign);

		var round = new TransactionWithPrecomputedData(transaction, transaction.PrecomputeTransactionData(coins), ImmutableDictionary<OutPoint, OwnershipProof>.Empty);
		return (round, coins, keyManager);
	}
}
