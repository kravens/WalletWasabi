using System.Collections.Generic;
using System.Linq;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionOutputs;

namespace WalletWasabi.Payjoin;

internal static class PayjoinSigner
{
	/// <summary>Signs and finalizes the inputs spending <paramref name="coins"/>; the other party's inputs stay as they are.</summary>
	public static PSBT Sign(PSBT psbt, Network network, KeyManager keyManager, string password, IReadOnlyCollection<SmartCoin> coins)
	{
		foreach (SmartCoin coin in coins)
		{
			PSBTInput input = psbt.Inputs.FindIndexedInput(coin.Outpoint)
				?? throw new InvalidOperationException($"The payjoin proposal does not spend our coin {coin.Outpoint}.");
			if (coin.ScriptPubKey.IsScriptType(ScriptType.Taproot))
			{
				input.TaprootInternalKey = coin.HdPubKey.PubKey.TaprootInternalKey;
			}
		}

		TransactionBuilder builder = network.CreateTransactionBuilder();
		builder.AddKeys(keyManager.GetSecrets(password, coins.Select(x => x.ScriptPubKey).ToArray()).ToArray());
		builder.AddCoins(coins.Select(x => x.Coin));
		builder.SignPSBT(psbt);

		var ours = coins.Select(x => x.Outpoint).ToHashSet();
		foreach (PSBTInput input in psbt.Inputs)
		{
			if (ours.Contains(input.PrevOut))
			{
				if (!input.TryFinalizeInput(out var errors))
				{
					throw new InvalidOperationException($"Payjoin input {input.PrevOut} could not be finalized: {string.Join("; ", errors)}");
				}
			}
			else if (!input.IsFinalized())
			{
				// The builder signs by key, so an input of ours we did not mean to sign gets its signature taken away again.
				input.PartialSigs.Clear();
				input.TaprootKeySignature = null;
			}
		}

		return psbt;
	}
}
