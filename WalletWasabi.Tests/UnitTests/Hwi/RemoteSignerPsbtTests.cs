using System.Linq;
using NBitcoin;
using WalletWasabi.WabiSabi.Client;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Hwi;

/// <summary>What a device that signs from the wallet's ordinary accounts is handed to sign a round from, and what is taken back.</summary>
public class RemoteSignerPsbtTests
{
	[Fact]
	public void EveryInputCarriesItsWitnessUtxo()
	{
		// A taproot signature commits to all of them, and a Coldcard on coinjoin-fw-2 fails a round without the foreign ones, segwit-only rounds too.
		var (round, coins, keyManager) = TestKeyManagers.Round();

		var psbt = RemoteSignerPsbt.Build(round, keyManager);

		Assert.All(psbt.Inputs, input => Assert.Equal(coins[(int)input.Index].TxOut, input.WitnessUtxo));
	}

	[Fact]
	public void OurInputsAndOutputsCarryTheirDerivationForeignOnesNothing()
	{
		// A device recognises its own taproot coins by their BIP-371 derivation only, and credits only its own outputs to the self-transfer its policy demands.
		var (round, _, keyManager) = TestKeyManagers.Round();

		var psbt = RemoteSignerPsbt.Build(round, keyManager);

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
		Assert.False(witnesses.ContainsKey(coins[2].Outpoint)); // left unsigned; the round cache reports it
	}
}
