using NBitcoin;
using WalletWasabi.WabiSabi.Client.CoinJoin.Client;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.WabiSabi.Client;

/// <summary>
/// The client's prediction of what a coin will cost us per byte, checked against the one case
/// hardware actually produced. On 2026-08-02 a Coldcard refused a 30,677 sat coin on regtest with
/// "feerate too high: 23570 sats/kvB of ours, limit is 5000", the round was disrupted, and the
/// coordinator prisoned the input seconds later. The client had every number needed to see that
/// coming and registered it anyway; these tests pin the estimate that stops it happening again.
/// </summary>
public class SignerAffordabilityTests
{
	private static readonly FeeRate OneSatPerVByte = new(1m);
	private static readonly Money MinRegistrable = Money.Satoshis(5_000);

	[Fact]
	public void TheCoinTheDeviceActuallyRefusedIsPredictedTooExpensive()
	{
		// 30,677 sats is far above the 5,000 minimum, so it is not obviously dust — but almost all of
		// it is stranded once decomposed, which is what made the real feerate 23,570 sats/kvB.
		var perKvByte = CoinJoinClient.WorstCaseLossPerKvByte(
			Money.Satoshis(30_677), ScriptType.P2WPKH, OneSatPerVByte, Money.Satoshis(50_000));

		Assert.True(perKvByte > 5_000,
			$"estimate {perKvByte} sats/kvB should exceed the 5,000 cap the device refused at");
	}

	[Fact]
	public void AnOrdinaryCoinIsNotDroppedAtTheSameLimit()
	{
		// The other half: a rule that refuses everything is useless. A 0.05 BTC coin at 1 sat/vB
		// loses only its own input and output fee, which is nowhere near the cap.
		var perKvByte = CoinJoinClient.WorstCaseLossPerKvByte(
			Money.Coins(0.05m), ScriptType.P2WPKH, OneSatPerVByte, MinRegistrable);

		Assert.True(perKvByte <= 5_000,
			$"estimate {perKvByte} sats/kvB should be well under the cap for a healthy coin");
	}

	[Fact]
	public void TheEstimateTracksTheNetworkFeeRate()
	{
		// With nothing stranded, the loss per byte is the mining fee rate — so the estimate reduces to
		// the obvious answer in the ordinary case rather than inventing a number.
		var atOne = CoinJoinClient.WorstCaseLossPerKvByte(
			Money.Coins(0.05m), ScriptType.P2WPKH, new FeeRate(1m), MinRegistrable);
		var atTen = CoinJoinClient.WorstCaseLossPerKvByte(
			Money.Coins(0.05m), ScriptType.P2WPKH, new FeeRate(10m), MinRegistrable);

		Assert.InRange(atOne, 900, 1_100);
		Assert.InRange(atTen, 9_000, 11_000);
	}

	[Fact]
	public void ADustCoinIsAlwaysOverAnySaneCap()
	{
		// A coin worth less than the minimum registrable amount cannot come back at all.
		var perKvByte = CoinJoinClient.WorstCaseLossPerKvByte(
			Money.Satoshis(3_000), ScriptType.P2WPKH, OneSatPerVByte, MinRegistrable);

		Assert.True(perKvByte > 20_000, $"estimate was only {perKvByte} sats/kvB for a dust coin");
	}
}
