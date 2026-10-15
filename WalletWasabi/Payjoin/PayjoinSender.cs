using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using Payjoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionBuilding;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Logging;
using WalletWasabi.Wallets;
using OutPoint = NBitcoin.OutPoint;
using Transaction = NBitcoin.Transaction;
using Uri = System.Uri;

namespace WalletWasabi.Payjoin;

public record PayjoinSendResult(SmartTransaction Transaction, bool IsPayjoin, string? FallbackReason);

/// <summary>
/// BIP 77 sender: posts the signed original transaction to the receiver's mailbox, waits for the
/// receiver's proposal, lets the library run the BIP 78 sender checks on it, signs our inputs again
/// and hands back the finished transaction. Whatever goes wrong, the answer is the original
/// transaction: it pays the receiver just the same and is what the receiver may broadcast too.
/// </summary>
public static class PayjoinSender
{
	/// <summary>
	/// Builds the original like any other send, except that the lock time never sits at or past our
	/// tip: there the original is not final for a receiver whose node lags a block and the payjoin
	/// fails before it starts. The rest of the usual distribution is kept.
	/// </summary>
	public static BuildTransactionResult BuildOriginal(Wallet wallet, string password, PaymentIntent payment, FeeRate feeRate, IEnumerable<OutPoint>? allowedInputs)
	{
		var parameters = new TransactionParameters(payment, feeRate, AllowUnconfirmed: true, AllowDoubleSpend: false, AllowedInputs: allowedInputs, TryToSign: true, OverrideFeeOverpaymentProtection: false);
		var factory = new TransactionFactory(wallet.Network, wallet.KeyManager, wallet.Coins, wallet.TransactionStore, password);
		return factory.BuildTransaction(parameters, () =>
		{
			var tip = wallet.FilterHeaderChain.TipHeight;
			return ChooseLockTime(LockTimeSelector.Instance.GetLockTimeBasedOnDistribution(tip), tip);
		});
	}

	/// <summary>Only a height lock below the tip is kept; anything else (at or past the tip, or the time lock the selector produces on very short chains) becomes zero.</summary>
	internal static LockTime ChooseLockTime(LockTime drawn, ChainHeight tip) =>
		drawn.IsHeightLock && drawn.Height < tip.Height ? drawn : LockTime.Zero;

	public static async Task<PayjoinSendResult> SendAsync(string bip21Uri, BuildTransactionResult original, Network network, KeyManager keyManager, string password, PayjoinConfiguration configuration, IHttpClientFactory httpClientFactory, TimeSpan timeout, CancellationToken cancellationToken)
	{
		if (!original.Psbt.IsAllFinalized())
		{
			// Neither the payjoin nor the fallback works with an unsigned transaction.
			throw new InvalidOperationException("The original transaction must be signed.");
		}

		var persister = new InMemorySenderPersister();
		string clientName = $"payjoin-oneshot-{Guid.NewGuid():N}";
		int[] relayIndex = [Random.Shared.Next(configuration.OhttpRelays.Length)];
		PollingForProposal? polling = null;
		using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		deadline.CancelAfter(timeout);
		try
		{
			using global::Payjoin.Uri uri = global::Payjoin.Uri.Parse(bip21Uri);
			using PjUri pjUri = uri.CheckPjSupported();
			using var builder = new SenderBuilder(WithoutKeyPaths(original.Psbt).ToBase64(), pjUri);

			// The payment goes to the address in the URI and nowhere else.
			using SenderBuilder noSubstitution = builder.AlwaysDisableOutputSubstitution();
			using InitialSendTransition initial = noSubstitution.BuildRecommended(MinFeeRateSatPerKwu(original));
			using WithReplyKey withReplyKey = initial.Save(persister);

			(RequestOhttpContext post, byte[] postResponse) = await PostWithFailoverAsync(withReplyKey.CreateV2PostRequest, configuration, httpClientFactory, clientName, relayIndex, deadline.Token).ConfigureAwait(false);
			using (post)
			{
				using WithReplyKeyTransition transition = withReplyKey.ProcessResponse(postResponse, post.OhttpCtx);
				polling = transition.Save(persister);
			}

			while (true)
			{
				(RequestOhttpContext poll, byte[] pollResponse) = await PostWithFailoverAsync(polling.CreatePollRequest, configuration, httpClientFactory, clientName, relayIndex, deadline.Token).ConfigureAwait(false);
				PollingForProposalTransitionOutcome outcome;
				using (poll)
				{
					using PollingForProposalTransition transition = polling.ProcessResponse(pollResponse, poll.OhttpCtx);
					outcome = transition.Save(persister);
				}

				switch (outcome)
				{
					case PollingForProposalTransitionOutcome.Progress progress:
						using (outcome)
						{
							return new PayjoinSendResult(SignProposal(progress.PsbtBase64, original, network, keyManager, password), IsPayjoin: true, FallbackReason: null);
						}

					case PollingForProposalTransitionOutcome.Stasis stasis:
						// Stasis carries the next polling state, so only the previous one goes away.
						polling.Dispose();
						polling = stasis.Inner;

						// The directory holds an empty mailbox request open for a while; the pause only matters for a relay that answers at once.
						await Task.Delay(TimeSpan.FromSeconds(2), deadline.Token).ConfigureAwait(false);
						break;

					default:
						throw new NotSupportedException($"Unknown payjoin polling outcome {outcome.GetType().Name}.");
				}
			}
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return new PayjoinSendResult(original.Transaction, IsPayjoin: false, "The receiver did not answer in time.");
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			string reason = PayjoinErrors.Describe(ex);
			Logger.LogWarning($"Payjoin failed, sending the original transaction instead. {reason}");
			return new PayjoinSendResult(original.Transaction, IsPayjoin: false, reason);
		}
		finally
		{
			polling?.Dispose();
		}
	}

	/// <summary>One relay for the whole send; the next one is tried only when a relay cannot be reached. Each attempt is a fresh encapsulation, as the protocol requires.</summary>
	private static async Task<(RequestOhttpContext Request, byte[] Body)> PostWithFailoverAsync(Func<string, RequestOhttpContext> createRequest, PayjoinConfiguration configuration, IHttpClientFactory httpClientFactory, string clientName, int[] relayIndex, CancellationToken cancellationToken)
	{
		for (int attempt = 0; ; attempt++)
		{
			RequestOhttpContext request = createRequest(configuration.GetRelay(relayIndex[0]).ToString());
			try
			{
				return (request, await PayjoinTransport.PostAsync(httpClientFactory, clientName, request.Request, cancellationToken).ConfigureAwait(false));
			}
			catch (Exception ex) when (attempt + 1 < configuration.OhttpRelays.Length && (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)))
			{
				request.Dispose();
				Logger.LogWarning($"Payjoin relay {configuration.GetRelay(relayIndex[0])} failed, staying on the next one. {ex.Message}");
				relayIndex[0]++;
			}
			catch
			{
				request.Dispose();
				throw;
			}
		}
	}

	private static SmartTransaction SignProposal(string proposalBase64, BuildTransactionResult original, Network network, KeyManager keyManager, string password)
	{
		PSBT proposal = PSBT.Parse(proposalBase64, network);
		PayjoinSigner.Sign(proposal, network, keyManager, password, original.SpentCoins.ToArray());

		// Throws unless every input, the receiver's included, is finalized.
		Transaction tx = proposal.ExtractTransaction();
		return new SmartTransaction(tx, Mempool, labels: original.Transaction.Labels);
	}

	/// <summary>The receiver has no business knowing our derivation paths or account keys (BIP 78); the library strips them as well, this just does not rely on it.</summary>
	private static PSBT WithoutKeyPaths(PSBT psbt)
	{
		PSBT stripped = psbt.Clone();
		stripped.GlobalXPubs.Clear();
		foreach (PSBTOutput output in stripped.Outputs)
		{
			output.HDKeyPaths.Clear();
			output.HDTaprootKeyPaths.Clear();
		}

		return stripped;
	}

	/// <summary>The receiver may not lower the fee rate below the one we chose. Fee rates travel as sat/kwu; 1 vbyte is 4 weight units.</summary>
	private static ulong MinFeeRateSatPerKwu(BuildTransactionResult original)
	{
		var feeRate = new FeeRate(original.Fee, original.Transaction.Transaction.GetVirtualSize());
		return Math.Max(250UL, (ulong)(feeRate.FeePerK.Satoshi / 4));
	}

	// ponytail: a send lives as long as the RPC call, so its event log stays in memory; write it to disk if sends should survive restarts.
	private sealed class InMemorySenderPersister : JsonSenderSessionPersister
	{
		private readonly List<string> _events = [];

		public void Save(string @event) => _events.Add(@event);

		public string[] Load() => _events.ToArray();

		public void Close()
		{
		}
	}
}
