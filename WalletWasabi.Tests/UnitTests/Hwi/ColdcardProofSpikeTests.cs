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
}
