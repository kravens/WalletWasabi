using System;
using System.Linq;
using System.Text;
using NBitcoin;
using WalletWasabi.Crypto;
using WalletWasabi.Extensions;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Hwi;

/// <summary>
/// Feasibility spike for feature/coldcard-coinjoin: proves the Coldcard firmware can produce a SLIP-19
/// ownership proof that Wasabi's own verifier accepts. The constants below were produced INSIDE the
/// Coldcard simulator firmware (fixed seed, path m/84'/0'/0'/1/0) by building the proof over Wasabi's
/// exact digest (SHA256(proofBody ‖ varint‖spk ‖ varint‖commitment)) and ECDSA-signing it — the same
/// computation a real `slip19_sign` firmware command would run.
/// </summary>
public class ColdcardProofSpikeTests
{
	[Fact]
	public void ColdcardFirmwareProducesWasabiVerifiableProof_P2wpkh()
	{
		// Captured from the Coldcard simulator (see WSL ~/coldcard/spike_proof.py).
		var proofBody = Convert.FromHexString("534c001901010000000000000000000000000000000000000000000000000000000000000000");
		var spk = new Script(Convert.FromHexString("00140f967e793629de58a6d9a1b001c81b58b310fc54"));
		var commitment = Encoding.ASCII.GetBytes("coldcard-spike-commitment");
		var pubKey = new PubKey("03327c51346d9d3ce21ab299b1d46d083608da3434564dd70821c6296742ffc839");
		var derSigWithSighash = Convert.FromHexString("3045022100e63d0f796d6408f2f81c4cd32af8a9af5a7b12f957210c7ddb3ce8ec696a3c35022070fb1ab304df684b4e74396d3e0d87bae42bf1b21a18d4be671b42dcd59f6d3c01");

		// The device's scriptPubKey really is this pubkey's P2WPKH.
		Assert.Equal(spk, pubKey.GetScriptPubKey(ScriptPubKeyType.Segwit));

		// Reassemble the ownership proof from the device's pieces the way the coordinator would receive it.
		var body = NBitcoinExtensions.FromBytes<ProofBody>(proofBody);
		var witness = new WitScript(Op.GetPushOp(derSigWithSighash), Op.GetPushOp(pubKey.ToBytes()));
		var proof = new OwnershipProof(body, new Bip322Signature(Script.Empty, witness));

		// The exact check ArenaClient runs on a registered input's ownership proof.
		Assert.True(proof.VerifyOwnership(spk, commitment, requireUserConfirmation: true));
	}

	[Fact]
	public void ColdcardSlp9UsbCommandWireProofVerifies_P2wpkh()
	{
		// Full serialized ownership proof returned by the real firmware `slp9` USB command
		// (Coldcard simulator, subpath m/84'/0'/0'/1/0, commitment below). Parsed exactly as the
		// coordinator would parse it off the wire.
		var wire = Convert.FromHexString("534c0019010100000000000000000000000000000000000000000000000000000000000000000002483045022100e63d0f796d6408f2f81c4cd32af8a9af5a7b12f957210c7ddb3ce8ec696a3c35022070fb1ab304df684b4e74396d3e0d87bae42bf1b21a18d4be671b42dcd59f6d3c012103327c51346d9d3ce21ab299b1d46d083608da3434564dd70821c6296742ffc839");
		var spk = new Script(Convert.FromHexString("00140f967e793629de58a6d9a1b001c81b58b310fc54"));
		var commitment = Encoding.ASCII.GetBytes("coldcard-spike-commitment");

		var proof = OwnershipProof.FromBytes(wire);
		Assert.True(proof.VerifyOwnership(spk, commitment, requireUserConfirmation: true));
	}

	[Fact]
	public void ColdcardSlp9UsbCommandWireProofVerifies_P2tr()
	{
		// Full serialized ownership proof from the real firmware taproot branch (Coldcard simulator,
		// subpath m/86'/0'/0'/1/0, same commitment). BIP-340 key-spend over the SLIP-19 digest.
		var wire = Convert.FromHexString("534c001901010000000000000000000000000000000000000000000000000000000000000000000140ab13296ebe2d3b5f01080869f419d66ca9242e816f8c1ef23724a77e99593dcd55535565e47d9026ae336016099104b2b9f7b353ef6c4cbad1a89e69a1f4518a");
		var commitment = Encoding.ASCII.GetBytes("coldcard-spike-commitment");

		// The device's INTERNAL (pre-tweak) pubkey at that path. Deriving the P2TR scriptPubKey from it
		// via NBitcoin's own BIP-86 tweak is the independent check: the proof only verifies if the firmware's
		// taproot tweak (libsecp keypair_xonly_tweak_add) matches NBitcoin's.
		var internalPubKey = new PubKey("024fecbea811f13c638b60d5f823f4a3cd10f646446b8f69e3575dee5fec0aad27");
		var spk = internalPubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);

		// Sanity: NBitcoin's tweaked output key equals the firmware's out_xonly.
		Assert.Equal("51204153cc8399f5c45ee260138551fb05e0a080d576244ab97308b417a57a0ddfef", spk.ToHex());

		var proof = OwnershipProof.FromBytes(wire);
		Assert.True(proof.VerifyOwnership(spk, commitment, requireUserConfirmation: true));
	}
}
