using WalletWasabi.WabiSabi.Models.MultipartyTransaction;

namespace WalletWasabi.WabiSabi.Client;

/// <summary>
/// The coinjoin as a PSBT for a device that signs from the wallet's ordinary accounts. Every input carries its
/// witness UTXO (a taproot sighash commits to all of them, and the device tallies the fee from them); our inputs
/// and our outputs carry their derivation, BIP-32 for segwit and BIP-371 for taproot, which is how the device
/// tells what is its own and credits the self-transfer its policy demands. Foreign inputs and outputs stay bare.
/// </summary>
internal static class RemoteSignerPsbt
{
	public static PSBT Build(TransactionWithPrecomputedData unsignedCoinJoin, KeyManager keyManager)
	{
		var spentOutputs = ((TaprootReadyPrecomputedTransactionData)unsignedCoinJoin.PrecomputedTransactionData).SpentOutputs;
		var masterFingerprint = keyManager.MasterFingerprint ?? throw new InvalidOperationException("The wallet has no master fingerprint.");
		var psbt = PSBT.FromTransaction(unsignedCoinJoin.Transaction, keyManager.GetNetwork());

		for (int i = 0; i < psbt.Inputs.Count; i++)
		{
			psbt.Inputs[i].WitnessUtxo = spentOutputs[i];
			AddDerivation(psbt.Inputs[i], spentOutputs[i].ScriptPubKey, keyManager, masterFingerprint);
		}

		foreach (var output in psbt.Outputs)
		{
			AddDerivation(output, output.ScriptPubKey, keyManager, masterFingerprint);
		}

		return psbt;
	}

	/// <summary>The witnesses a device put on the inputs it signed, whether as final witnesses, taproot key signatures or partial signatures.</summary>
	public static Dictionary<OutPoint, WitScript> Witnesses(PSBT signedPsbt)
	{
		var witnesses = new Dictionary<OutPoint, WitScript>();
		foreach (var input in signedPsbt.Inputs)
		{
			if (input.FinalScriptWitness is { } finalWitness)
			{
				witnesses[input.PrevOut] = finalWitness;
			}
			else if (input.TaprootKeySignature is { } taprootSignature)
			{
				witnesses[input.PrevOut] = new WitScript(Op.GetPushOp(taprootSignature.ToBytes()));
			}
			else if (input.PartialSigs.FirstOrDefault() is { Key: not null } partialSig)
			{
				witnesses[input.PrevOut] = new WitScript(Op.GetPushOp(partialSig.Value.ToBytes()), Op.GetPushOp(partialSig.Key.ToBytes()));
			}
		}

		if (witnesses.Count == 0)
		{
			throw new InvalidOperationException("The device returned no signatures.");
		}

		return witnesses;
	}

	private static void AddDerivation(PSBTCoin psbtCoin, Script scriptPubKey, KeyManager keyManager, HDFingerprint masterFingerprint)
	{
		if (!keyManager.TryGetKeyForScriptPubKey(scriptPubKey, out var hdPubKey))
		{
			return; // foreign, the device treats it as such
		}

		var rootedKeyPath = new RootedKeyPath(masterFingerprint, hdPubKey.FullKeyPath);
		if (scriptPubKey.IsScriptType(ScriptType.Taproot))
		{
			// BIP-371 keys taproot derivations by the x-only internal key; NBitcoin's map just wants 32 bytes.
			psbtCoin.HDTaprootKeyPaths.Add(new TaprootPubKey(hdPubKey.PubKey.TaprootInternalKey.ToBytes()), new TaprootKeyPath(rootedKeyPath));
		}
		else
		{
			psbtCoin.HDKeyPaths.Add(hdPubKey.PubKey, rootedKeyPath);
		}
	}
}
