using WalletWasabi.WabiSabi.Models.MultipartyTransaction;

namespace WalletWasabi.WabiSabi.Client;

/// <summary>
/// The signing phase asks for one coin at a time, while a device signs all our inputs of a round in one call
/// that may spend a round of its authorization. This signs a round once and serves every coin from it.
/// </summary>
internal sealed class SignedRoundCache(Func<TransactionWithPrecomputedData, Dictionary<OutPoint, WitScript>> signOnDevice)
{
	private readonly object _lock = new();
	private (uint256 TxId, Dictionary<OutPoint, WitScript> Witnesses)? _round;

	public Transaction Sign(TransactionWithPrecomputedData unsignedCoinJoin, Coin coin)
	{
		lock (_lock)
		{
			var transaction = unsignedCoinJoin.Transaction;
			if (_round is not { } round || round.TxId != transaction.GetHash())
			{
				round = (transaction.GetHash(), signOnDevice(unsignedCoinJoin));
				_round = round;
			}

			if (!round.Witnesses.TryGetValue(coin.Outpoint, out var witness))
			{
				throw new HardwareWalletException($"The device did not sign the input '{coin.Outpoint}'.");
			}

			transaction = transaction.Clone();
			var txInput = transaction.Inputs.AsIndexedInputs().FirstOrDefault(input => input.PrevOut == coin.Outpoint)
				?? throw new InvalidOperationException("Missing input.");
			txInput.WitScript = witness;
			return transaction;
		}
	}
}
