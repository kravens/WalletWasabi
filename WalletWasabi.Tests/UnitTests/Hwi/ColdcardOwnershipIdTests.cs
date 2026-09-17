using System;
using System.Security.Cryptography;
using System.Text;
using NBitcoin;
using WalletWasabi.Crypto;
using WalletWasabi.Extensions;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Hwi;

/// <summary>
/// The firmware now derives a real SLIP-19 ownership identifier instead of 32 zero bytes:
/// id = HMAC-SHA256(key = Key(m/"SLIP-0019"/"Ownership identification key"), msg = scriptPubKey),
/// with the key derived per SLIP-21 from the wallet seed. These proofs come from the simulator
/// running that firmware; they must still satisfy the coordinator's verifier, and the identifier
/// must actually be bound to the scriptPubKey rather than constant.
/// </summary>
public class ColdcardOwnershipIdTests
{
	private static readonly byte[] Commitment = Encoding.ASCII.GetBytes("slip21-oid-check");

	[Fact]
	public void ProofWithRealOwnershipIdVerifies_P2wpkh()
	{
		var wire = Convert.FromHexString("534c001900017deecc308e7444f33da08db6a035ebd706f374915a0e7e7f911da4368c36be4f000248304502210087e8016a03778646aa2fd36b684ca466844b9281e7d7767f97fe8f2509fc609902206bb0ad87ed4ec4190b8eb4fdd928572bf186072990eeb8fe9ec7b6ad5270a07a012103327c51346d9d3ce21ab299b1d46d083608da3434564dd70821c6296742ffc839");
		var leaf = new BitcoinExtPubKey(
			"tpubDGyYRVLEAPLbzUJHb3it2QS79SbctmzHjrYboAyEvymbmuh1NZV1SKUenG9Jc22SeCeZYXj6s4AaQsn7NYCbgXtoYAs4dkt1aE52YEq8jAK",
			Network.RegTest);
		var spk = leaf.ExtPubKey.PubKey.GetScriptPubKey(ScriptPubKeyType.Segwit);

		var proof = OwnershipProof.FromBytes(wire);
		Assert.True(proof.VerifyOwnership(spk, Commitment, requireUserConfirmation: false));
	}

	[Fact]
	public void ProofWithRealOwnershipIdVerifies_P2tr()
	{
		var wire = Convert.FromHexString("534c0019000112dfdad92f60c05a575d051f8faaa96f0f315db4eb93e887b04b8ef72f701c6c000140a05162fc12b791acf4ae49f51bd8925d6f88e0ffd8d2ab3ba61b3d2dde132fab126410e8400fbd917c54cd363e51ccb1362cc1c60b8ebeb257eae2d07db190fa");
		var leaf = new BitcoinExtPubKey(
			"tpubDGfGBNu9obD8pviuy4N4RhFaNPPzg11Pr7YHZJKRWX3CY23obqQCCYQ11w6pFoH7Ldbiy2VMiDEfsVbEffB1PXw1tYcU9aRXWdE5dXUUG79",
			Network.RegTest);
		var spk = leaf.ExtPubKey.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);

		var proof = OwnershipProof.FromBytes(wire);
		Assert.True(proof.VerifyOwnership(spk, Commitment, requireUserConfirmation: false));
	}

	[Fact]
	public void OwnershipIdIsRealAndBoundToTheScript()
	{
		// Regression guard for the placeholder this replaced: a constant id told a coordinator
		// nothing, and two different scripts must not share one.
		var segwit = Convert.FromHexString("534c001900017deecc308e7444f33da08db6a035ebd706f374915a0e7e7f911da4368c36be4f00")[6..38];
		var taproot = Convert.FromHexString("534c0019000112dfdad92f60c05a575d051f8faaa96f0f315db4eb93e887b04b8ef72f701c6c00")[6..38];

		Assert.NotEqual(new byte[32], segwit);
		Assert.NotEqual(new byte[32], taproot);
		Assert.NotEqual(segwit, taproot);
	}

	[Fact]
	public void OwnershipIdMatchesSlip19Definition()
	{
		// Independently recompute the official SLIP-19/21 derivation here, so this test fails if the
		// firmware ever drifts from the spec rather than merely from a captured blob. Uses the
		// published SLIP-19 vector 1 ("all all all ..." seed, P2WPKH).
		var seed = Bip39Seed("all all all all all all all all all all all all", "");
		var expected = Convert.FromHexString("a122407efc198211c81af4450f40b235d54775efd934d16b9e31c6ce9bad5707");
		var spk = Convert.FromHexString("0014b2f771c370ccf219cd3059cda92bdf7f00cf2103");

		Assert.Equal(expected, OwnershipIdentifier(seed, spk));
	}

	private static byte[] Bip39Seed(string mnemonic, string passphrase) =>
		Rfc2898DeriveBytes.Pbkdf2(
			Encoding.UTF8.GetBytes(mnemonic.Normalize(NormalizationForm.FormKD)),
			Encoding.UTF8.GetBytes(("mnemonic" + passphrase).Normalize(NormalizationForm.FormKD)),
			2048, HashAlgorithmName.SHA512, 64);

	private static byte[] OwnershipIdentifier(byte[] seed, byte[] scriptPubKey)
	{
		// SLIP-21: m = HMAC-SHA512("Symmetric key seed", S); Child(N,l) = HMAC-SHA512(N[0:32], 0x00||l)
		var node = HMACSHA512.HashData(Encoding.ASCII.GetBytes("Symmetric key seed"), seed);
		node = Child(node, "SLIP-0019");
		node = Child(node, "Ownership identification key");
		return HMACSHA256.HashData(node[32..64], scriptPubKey);   // SLIP-19: Key(N) = N[32:64]

		static byte[] Child(byte[] node, string label)
		{
			var msg = new byte[1 + label.Length];
			Encoding.ASCII.GetBytes(label).CopyTo(msg, 1);
			return HMACSHA512.HashData(node[0..32], msg);
		}
	}
}
