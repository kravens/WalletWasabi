using NBitcoin;
using WalletWasabi.Payjoin;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Payjoin;

public class PayjoinReceiverManagerTests
{
	private static readonly byte[] Signature = new byte[71];
	private static readonly byte[] PublicKey = new byte[33];
	private static readonly byte[] SchnorrSignature = new byte[64];

	[Fact]
	public void RecognizesTheSendersInputType()
	{
		Assert.Equal(ScriptType.P2WPKH, PayjoinReceiverManager.GetUniformInputType(CreateTransaction(new WitScript(new[] { Signature, PublicKey }), new WitScript(new[] { Signature, PublicKey }))));
		Assert.Equal(ScriptType.Taproot, PayjoinReceiverManager.GetUniformInputType(CreateTransaction(new WitScript(new[] { SchnorrSignature }), new WitScript(new[] { new byte[65] }))));
	}

	[Fact]
	public void RefusesInputsThatNoCoinOfOursMatches()
	{
		// Mixed, legacy, nested or script path spends, a P2WSH witness of two items, and an input-less transaction.
		Assert.Null(PayjoinReceiverManager.GetUniformInputType(CreateTransaction(new WitScript(new[] { Signature, PublicKey }), new WitScript(new[] { SchnorrSignature }))));
		Assert.Null(PayjoinReceiverManager.GetUniformInputType(CreateTransaction(WitScript.Empty)));
		Assert.Null(PayjoinReceiverManager.GetUniformInputType(CreateTransaction(new WitScript(new[] { Signature, new byte[200] }))));
		Assert.Null(PayjoinReceiverManager.GetUniformInputType(CreateTransaction(new WitScript(new[] { SchnorrSignature, new byte[34], new byte[33] }))));
		Assert.Null(PayjoinReceiverManager.GetUniformInputType(Network.Main.CreateTransaction()));

		Transaction nested = CreateTransaction(new WitScript(new[] { Signature, PublicKey }));
		nested.Inputs[0].ScriptSig = new Script(Op.GetPushOp(new byte[22]));
		Assert.Null(PayjoinReceiverManager.GetUniformInputType(nested));
	}

	private static Transaction CreateTransaction(params WitScript[] witnesses)
	{
		Transaction tx = Network.Main.CreateTransaction();
		foreach (WitScript witness in witnesses)
		{
			tx.Inputs.Add(new TxIn(BitcoinFactory.CreateOutPoint()) { WitScript = witness });
		}

		return tx;
	}
}
