using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Hwi.Coldcard;
using WalletWasabi.WabiSabi.Client.CoinJoin.Client;
using WalletWasabi.WabiSabi.Client.CoinJoin.Manager;
using WalletWasabi.WabiSabi.Client.RoundStateAwaiters;
using WalletWasabi.WabiSabi.Coordinator.PostRequests;

namespace WalletWasabi.WabiSabi.Client;

public class CoinJoinTrackerFactory
{
	public CoinJoinTrackerFactory(
		Func<string, IWabiSabiApiRequestHandler> arenaRequestHandlerFactory,
		RoundStateProvider roundStatusProvider,
		CoinJoinConfiguration coinJoinConfiguration,
		CancellationToken cancellationToken)
	{
		ArenaRequestHandlerFactory = arenaRequestHandlerFactory;
		_roundStatusProvider = roundStatusProvider;
		_coinJoinConfiguration = coinJoinConfiguration;
		_cancellationToken = cancellationToken;
		_liquidityClueProvider = new LiquidityClueProvider();
	}

	private Func<string, IWabiSabiApiRequestHandler> ArenaRequestHandlerFactory { get; }
	private readonly RoundStateProvider _roundStatusProvider;
	private readonly CoinJoinConfiguration _coinJoinConfiguration;
	private readonly CancellationToken _cancellationToken;
	private readonly LiquidityClueProvider _liquidityClueProvider;

	public CoinJoinTracker CreateAndStart(Wallet wallet, Wallet outputWallet, Func<IEnumerable<SmartCoin>> coinCandidatesFunc, bool stopWhenAllMixed, bool overridePlebStop)
	{
		_liquidityClueProvider.InitLiquidityClue(wallet);

		if (wallet.KeyChain is null)
		{
			throw new NotSupportedException("Wallet has no key chain.");
		}

		// The only use-case when we set consolidation mode to true, when we are mixing to another wallet.
		wallet.ConsolidationMode = outputWallet.WalletId != wallet.WalletId;

		// The fee rate cap the user confirmed for a hardware signer also bounds which rounds we enter,
		// so a wallet-level cap below the global one is actually enforced (the Coldcard HSM policy has
		// no fee rate concept; for Trezor the device enforces it too, this just skips those rounds early).
		var coinJoinConfiguration = _coinJoinConfiguration;
		if (wallet.KeyManager.IsHardwareCoinJoinWallet())
		{
			coinJoinConfiguration = coinJoinConfiguration with
			{
				MaxCoinJoinMiningFeeRate = Math.Min(coinJoinConfiguration.MaxCoinJoinMiningFeeRate, wallet.KeyManager.TrezorCoinjoinMaxMiningFeeRate),
			};
		}

		var coinSelector = CoinJoinCoinSelector.FromWallet(wallet);
		var coinJoinClient = new CoinJoinClient(
			ArenaRequestHandlerFactory,
			wallet.KeyChain,
			outputWallet.OutputProvider,
			_roundStatusProvider,
			coinSelector,
			coinJoinConfiguration,
			_liquidityClueProvider,
			doNotRegisterInLastMinuteTimeLimit: TimeSpan.FromMinutes(1));

		return new CoinJoinTracker(wallet, coinJoinClient, coinCandidatesFunc, stopWhenAllMixed, overridePlebStop, outputWallet, _cancellationToken);
	}
}
