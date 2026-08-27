using System;
using System.Text;
using NBitcoin;
using WalletWasabi.Crypto;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Hwi;

/// <summary>
/// The first SLIP-19 ownership proof produced by the <c>slp9</c> command on REAL Coldcard hardware
/// (Mk4 serial 2050395F4833, firmware 5.5.1 built 2026-07-22 from the feature/slip19-coinjoin PR,
/// key-zero signed, regtest). Its sibling <see cref="ColdcardProofSpikeTests"/> pins simulator output;
/// this pins silicon, which is what the coordinator will actually be talking to.
/// </summary>
public class ColdcardHardwareProofTests
{
	[Fact]
	public void RealMk4Slp9ProofVerifies_P2wpkh()
	{
		// Captured live: subpath m/84h/0h/0h/1/0, commitment below, userConfirmation flag set.
		var wire = Convert.FromHexString("534c00190101000000000000000000000000000000000000000000000000000000000000000000024830450221009088492493fe8026c89fe73f43f4605ab3d5c4813fd92ec2035613ccfd9eb69002203c77ee97ad6cd4016aec5adf35c011c6d732d78c87d1f35aa2a28d3146ee2eac0121032116145e1f357b3b5a6f025935c66e22d983bb0256015225cc5ca3523f960b9f");
		var commitment = Encoding.ASCII.GetBytes("coldcard-hw-test");

		// The device's own xpub at that leaf; deriving the scriptPubKey from it rather than hardcoding
		// keeps this an independent check that the proof is over the key the device says it holds.
		var leafXpub = new BitcoinExtPubKey(
			"tpubDG8zDF9TXmsTmBujbjmxsYHvuZSjBqRAExrbBmx2Kh8UMy311Fdbq5U8QhYxtdbx7jSkH4JwGDkz5QocL8cq8Psi8H8kmxK8Nw9HwBvEA2s",
			Network.RegTest);
		var spk = leafXpub.ExtPubKey.PubKey.GetScriptPubKey(ScriptPubKeyType.Segwit);

		// Exactly the check ArenaClient runs on a registered input's ownership proof.
		var proof = OwnershipProof.FromBytes(wire);
		Assert.True(proof.VerifyOwnership(spk, commitment, requireUserConfirmation: true));
	}

	[Fact]
	public void RealMk4Slp9ProofVerifies_P2tr()
	{
		// Same device and commitment, subpath m/86h/0h/0h/1/0: a BIP-340 key-spend signature over the
		// SLIP-19 digest. Deriving the output key through NBitcoin's own BIP-86 tweak is the independent
		// check — the proof only verifies if the firmware's tweak matches NBitcoin's.
		var wire = Convert.FromHexString("534c00190101000000000000000000000000000000000000000000000000000000000000000000014038d145c5c6b9407b084c69ec91fc72dbf636e90205b54f22aac84e50200cebc5ff9aa566111127e239241e52a2a92d87d26fc190f1b0cf171b413d1cf7524fbc");
		var commitment = Encoding.ASCII.GetBytes("coldcard-hw-test");

		var leafXpub = new BitcoinExtPubKey(
			"tpubDHMjDHTut7J79XGfRePioy6s3gS8d66dXnfVSnNjG9iASv79M7mTAg3cFGC3addxCWtUWyeUGPdGU36hJkTG4QA9YSgQT9Ghu694Ndz33bS",
			Network.RegTest);
		var spk = leafXpub.ExtPubKey.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);

		var proof = OwnershipProof.FromBytes(wire);
		Assert.True(proof.VerifyOwnership(spk, commitment, requireUserConfirmation: true));
	}
}
