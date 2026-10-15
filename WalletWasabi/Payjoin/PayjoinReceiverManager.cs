using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.RPC;
using Payjoin;
using WalletWasabi.Bases;
using WalletWasabi.BitcoinRpc;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionBroadcasting;
using WalletWasabi.Blockchain.TransactionBuilding;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Stores;
using WalletWasabi.Logging;
using WalletWasabi.Services;
using WalletWasabi.WabiSabi.Client.CoinJoin.Manager;
using WalletWasabi.Wallets;
using OutPoint = NBitcoin.OutPoint;
using Transaction = NBitcoin.Transaction;
using TxIn = NBitcoin.TxIn;
using Uri = System.Uri;

namespace WalletWasabi.Payjoin;

/// <summary>
/// Runs BIP 77 receiver sessions in the background. The payjoin library is a pure state machine:
/// each state hands us a request to send, the response moves the session on, and every step is
/// written to the session's event log before the next one runs. A session is therefore driven by
/// replaying its log and acting on the state that comes out, which also makes a restart a no-op:
/// the next tick continues from the last persisted step.
/// Every session uses its own HTTP client name, so over Tor every session gets its own circuit.
/// </summary>
public class PayjoinReceiverManager : PeriodicRunner
{
	private readonly Network _network;
	private readonly PayjoinConfiguration _configuration;
	private readonly WalletProvider _getWalletsAsync;
	private readonly IHttpClientFactory _httpClientFactory;
	private readonly TransactionBroadcaster _broadcaster;
	private readonly AllTransactionStore _transactionStore;
	private readonly IRPCClient? _rpcClient;
	private readonly IDisposable _walletLoadedSubscription;
	/// <summary>Transport failures per session; each one moves the session on to the next relay, around the list if need be.</summary>
	private readonly ConcurrentDictionary<string, int> _relayFailures = new();

	/// <summary>A fallback that could not be broadcast is retried at a calm pace, not every tick.</summary>
	private readonly ConcurrentDictionary<string, DateTimeOffset> _fallbackRetryAfter = new();
	private static readonly TimeSpan FallbackRetryInterval = TimeSpan.FromMinutes(10);

	/// <summary>A session never needs more steps than it has states; more means a step is not moving it on.</summary>
	private const int MaxStepsPerTick = 32;

	/// <summary>How long a coin signed into a proposal stays reserved while the fallback cannot be broadcast.</summary>
	private static readonly TimeSpan FallbackHoldTime = TimeSpan.FromDays(1);

	/// <summary>How long a session accepts a sender; the library's default, pinned here so the orphan handling below agrees with it.</summary>
	private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(24);

	/// <summary>A P2WPKH input; a coin worth less than its own fee at the cap would be a gift to the miners.</summary>
	private const int ContributedInputVirtualSize = 68;

	/// <summary>Serializes coin selection so two sessions cannot pick the same coin.</summary>
	private readonly Lock _selectionLock = new();

	/// <param name="rpcClient">The user's own node, when configured; it tells whether a sender's original transaction would be accepted.</param>
	public PayjoinReceiverManager(string dataDir, Network network, PayjoinConfiguration configuration, WalletProvider getWalletsAsync, IHttpClientFactory httpClientFactory, TransactionBroadcaster broadcaster, AllTransactionStore transactionStore, IRPCClient? rpcClient, EventBus eventBus)
		: base(TimeSpan.FromSeconds(5))
	{
		_network = network;
		_configuration = configuration;
		_getWalletsAsync = getWalletsAsync;
		_httpClientFactory = httpClientFactory;
		_broadcaster = broadcaster;
		_transactionStore = transactionStore;
		_rpcClient = rpcClient;
		Store = new PayjoinSessionStore(dataDir, network);

		// Reservations live on the coin objects, which a wallet load recreates: put them back at once.
		_walletLoadedSubscription = eventBus.Subscribe<WalletLoaded>(e =>
		{
			foreach (PayjoinSession session in Store.GetOpen())
			{
				SetReservation(session, [e.Wallet], reserved: true);
			}

			TriggerRound();
		});
	}

	public PayjoinSessionStore Store { get; }

	private ulong MaxFeeRateSatPerVb => (ulong)Math.Ceiling(_configuration.MaxFeeRate.SatoshiPerByte);

	/// <summary>Opens a session and returns it; its <see cref="PayjoinSession.PjUri"/> is what the sender gets. The key is taken only once the directory answered, so a failed attempt burns no address.</summary>
	public async Task<PayjoinSession> StartSessionAsync(Wallet wallet, Func<HdPubKey> takeKey, Money? amount, CancellationToken cancellationToken)
	{
		if (wallet.KeyManager.IsWatchOnly)
		{
			throw new InvalidOperationException("Receiving a payjoin needs a wallet that can sign its own input.");
		}

		if (amount is { } && amount <= Money.Zero)
		{
			throw new ArgumentException("The amount must be positive.", nameof(amount));
		}

		using OhttpKeys ohttpKeys = await PayjoinTransport.FetchOhttpKeysAsync(_configuration, _httpClientFactory, cancellationToken).ConfigureAwait(false);

		string address = takeKey().GetAddress(_network).ToString();
		PayjoinSession session = Store.Create(wallet.WalletName, address);

		using var builder = new ReceiverBuilder(address, _configuration.DirectoryUri.ToString(), ohttpKeys);
		using ReceiverBuilder withFeeCap = builder.WithMaxFeeRate(MaxFeeRateSatPerVb);
		using ReceiverBuilder withExpiry = withFeeCap.WithExpiration((ulong)SessionLifetime.TotalSeconds);
		using ReceiverBuilder? withAmount = amount is { } a ? withExpiry.WithAmount((ulong)a.Satoshi) : null;
		using InitialReceiveTransition transition = (withAmount ?? withExpiry).Build();
		using Initialized initialized = transition.Save(session);
		using PjUri pjUri = initialized.PjUri();

		session.PjUri = pjUri.AsString();
		session.Persist();
		Logger.LogInfo($"Payjoin session {session.Id} opened for wallet '{wallet.WalletName}'.");
		TriggerRound();
		return session;
	}

	/// <summary>Builds a transaction and reserves its inputs in one go, so no receiver session can pick one of them in between.</summary>
	public BuildTransactionResult BuildReserved(Func<BuildTransactionResult> build)
	{
		lock (_selectionLock)
		{
			BuildTransactionResult result = build();
			foreach (SmartCoin coin in result.SpentCoins)
			{
				coin.PayjoinInProgress = true;
			}

			return result;
		}
	}

	/// <summary>Gives an open session up on the next tick; a received original is still broadcast, as it pays us.</summary>
	public void RequestCancel(string sessionId, string walletName)
	{
		PayjoinSession session = Store.GetOpen().FirstOrDefault(x => x.Id == sessionId && x.WalletName == walletName)
			?? throw new InvalidOperationException($"There is no open payjoin session '{sessionId}' in this wallet.");
		Store.RequestCancel(session.Id);
		TriggerRound();
	}

	protected override async Task ActionAsync(CancellationToken cancel)
	{
		PayjoinSession[] sessions = Store.GetOpen().ToArray();
		if (sessions.Length == 0)
		{
			return;
		}

		Wallet[] wallets = (await _getWalletsAsync().ConfigureAwait(false)).ToArray();

		// Reservations live on the coin objects, which a restart or a wallet load recreates: put them
		// all back before any session picks a coin, on whichever loaded wallet holds the coin by now.
		foreach (PayjoinSession session in sessions)
		{
			SetReservation(session, wallets, reserved: true);
		}

		// Sessions spend most of their time waiting on the directory, so they run side by side.
		await Task.WhenAll(sessions.Select(session => RunAsync(session, wallets, cancel))).ConfigureAwait(false);
	}

	private async Task RunAsync(PayjoinSession session, Wallet[] wallets, CancellationToken cancel)
	{
		try
		{
			if (wallets.FirstOrDefault(x => x.WalletName == session.WalletName) is { Loaded: true } wallet)
			{
				await DriveAsync(session, wallet, wallets, cancel).ConfigureAwait(false);
			}
			else if (DateTimeOffset.UtcNow - session.Created > SessionLifetime + TimeSpan.FromHours(1))
			{
				// The wallet was deleted, renamed or never opened again, and the session has run out either way.
				// Without the wallet we cannot tell whether the payment settled, so nothing is broadcast on its
				// behalf; the sender can always broadcast their own original.
				Finish(session, wallets, "The wallet is no longer available.");
			}
		}
		catch (OperationCanceledException) when (cancel.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			// Network trouble and the like: the session is retried next tick, on the next relay if this one is the problem.
			if (ex is HttpRequestException or OperationCanceledException)
			{
				_relayFailures.AddOrUpdate(session.Id, 1, (_, failures) => failures + 1);
			}

			Logger.LogWarning($"Payjoin session {session.Id}: {PayjoinErrors.Describe(ex)}");
		}
	}

	private async Task DriveAsync(PayjoinSession session, Wallet wallet, Wallet[] allWallets, CancellationToken cancel)
	{
		for (int step = 0; !cancel.IsCancellationRequested; step++)
		{
			if (step == MaxStepsPerTick)
			{
				Logger.LogWarning($"Payjoin session {session.Id}: still not settled after {step} steps, continuing next tick.");
				return;
			}

			ReplayResult replay;
			try
			{
				replay = PayjoinMethods.ReplayReceiverEventLog(session);
			}
			catch (ReceiverReplayException ex)
			{
				if (session.LoadError is { } loadError)
				{
					// The log could not be read this time; nothing says it is broken.
					Logger.LogWarning($"Payjoin session {session.Id}: {loadError.Message}");
					return;
				}

				// Expired, or a log that cannot be read any more: either way the session is over.
				// The sender's signed original still pays us even though the payjoin is off.
				if (session.FallbackTx is { } fallbackTx && !IsCompleted(session) && !await BroadcastFallbackAsync(session, fallbackTx, cancel).ConfigureAwait(false) && KeepCoinWhileFallbackIsOut(session))
				{
					return;
				}

				Finish(session, allWallets, ex.IsExpired() ? "The session expired." : $"The session's event log cannot be read: {PayjoinErrors.Describe(ex)}");
				return;
			}

			bool advanced;
			using (replay)
			using (ReceiveSession state = replay.State())
			{
				if (state is ReceiveSession.Closed)
				{
					Finish(session, allWallets, error: null);
					return;
				}

				try
				{
					if (session.CancelRequested && Cancel(session, state))
					{
						session.Error ??= "Cancelled.";
						session.Persist();
						advanced = true;
					}
					else
					{
						advanced = await StepAsync(session, wallet, allWallets, state, cancel).ConfigureAwait(false);
					}
				}
				catch (ReceiverCreateRequestException ex) when (ex.IsExpired())
				{
					session.Error ??= "The session expired.";
					session.Persist();
					advanced = Cancel(session, state);
				}
				catch (ReceiverPersistedException.Fatal ex)
				{
					// The failure is in the log; the replay yields the error reply to send.
					string reason = PayjoinErrors.Describe(ex);
					Logger.LogInfo($"Payjoin session {session.Id}: the proposal was rejected. {reason}");
					session.Error ??= reason;
					session.Persist();
					advanced = true;
				}
				catch (ReceiverPersistedException.Transient ex)
				{
					Logger.LogDebug($"Payjoin session {session.Id}: {PayjoinErrors.Describe(ex)}");
					advanced = false;
				}
				catch (Exception ex) when (ex is InvalidOperationException or CoinSelectionException or InputContributionException)
				{
					// Nothing to contribute, the reserved coin is gone, the original cannot take our input:
					// give the session up so the sender falls back to the plain payment.
					string reason = PayjoinErrors.Describe(ex);
					Logger.LogWarning($"Payjoin session {session.Id}: {reason}");
					session.Error ??= reason;
					session.Persist();
					advanced = Cancel(session, state);
				}
			}

			// The library closes the log itself when the session is over.
			if (session.IsClosed)
			{
				Finish(session, allWallets, error: null);
				return;
			}

			// Waiting for the sender, the wallet or the network.
			if (!advanced)
			{
				return;
			}
		}
	}

	/// <returns>True when the session reached a new persisted state and can be driven further right away.</returns>
	private async Task<bool> StepAsync(PayjoinSession session, Wallet wallet, Wallet[] allWallets, ReceiveSession state, CancellationToken cancel)
	{
		switch (state)
		{
			case ReceiveSession.Initialized s:
				return await PollForOriginalAsync(session, s.Inner, cancel).ConfigureAwait(false);

			case ReceiveSession.UncheckedOriginalPayload s:
				await CheckOriginalAsync(session, wallet, s.Inner, cancel).ConfigureAwait(false);
				return true;

			case ReceiveSession.MaybeInputsOwned s:
			{
				// A sender must not make us sign over a coin that is already ours, and a payment from one of
				// our own wallets to another would tie the two together on chain.
				using MaybeInputsOwnedTransition transition = s.Inner.CheckInputsNotOwned(new OutPointCallback(outpoint => allWallets.Any(x => x.Loaded && x.Coins.TryGetByOutPoint(outpoint, out _))));
				transition.Save(session).Dispose();
				return true;
			}

			case ReceiveSession.MaybeInputsSeen s:
			{
				// Claimed right here, so two sessions cannot both accept the same original; released again if this session reveals nothing.
				using MaybeInputsSeenTransition transition = s.Inner.CheckNoInputsSeenBefore(new OutPointCallback(outpoint => !Store.TryMarkInputSeen(outpoint, session.Id)));
				transition.Save(session).Dispose();
				return true;
			}

			case ReceiveSession.OutputsUnknown s:
			{
				// Only the address this session handed out is the payment; any other output of ours the sender may have added is theirs to explain.
				Script sessionScript = BitcoinAddress.Create(session.Address, _network).ScriptPubKey;
				using OutputsUnknownTransition transition = s.Inner.IdentifyReceiverOutputs(new ScriptCallback(script => script == sessionScript));
				transition.Save(session).Dispose();
				return true;
			}

			case ReceiveSession.WantsOutputs s:
			{
				// The payment stays on the address we handed out; nothing to substitute.
				using WantsOutputsTransition transition = s.Inner.CommitOutputs();
				transition.Save(session).Dispose();
				return true;
			}

			case ReceiveSession.WantsInputs s:
				ContributeInput(session, wallet, s.Inner);
				return true;

			case ReceiveSession.WantsFeeRange s:
			{
				// The most we pay for our own contribution; the sender's own fee rate is their business.
				using WantsFeeRangeTransition transition = s.Inner.ApplyFeeRange(null, MaxFeeRateSatPerVb);
				transition.Save(session).Dispose();
				return true;
			}

			case ReceiveSession.ProvisionalProposal s:
				return SignContribution(session, wallet, s.Inner);

			case ReceiveSession.PayjoinProposal s:
				await PostProposalAsync(session, s.Inner, cancel).ConfigureAwait(false);
				return true;

			case ReceiveSession.Monitor s:
			{
				using MonitorTransition transition = s.Inner.CheckForTransaction(new TransactionFinderCallback(txid =>
					_transactionStore.TryGetTransaction(txid, out SmartTransaction? tx) ? tx.Transaction : null));
				transition.Save(session);

				// The library closes the log once the transaction shows up; until then there is nothing to do.
				return false;
			}

			case ReceiveSession.HasReplyableError s:
			{
				using RequestResponse request = s.Inner.CreateErrorRequest(RelayFor(session));
				byte[] body = await PayjoinTransport.PostAsync(_httpClientFactory, ClientName(session), request.Request, cancel).ConfigureAwait(false);
				using HasReplyableErrorTransition transition = s.Inner.ProcessErrorResponse(body, request.ClientResponse);
				transition.Save(session)?.Dispose();
				return true;
			}

			case ReceiveSession.ReceiverPendingFallback s:
				// The payjoin is off, but the sender's signed original still pays us, unless the payjoin went through after all.
				if (!IsCompleted(session) && !await BroadcastFallbackAsync(session, Transaction.Load(s.Inner.FallbackTx(), _network), cancel).ConfigureAwait(false) && KeepCoinWhileFallbackIsOut(session))
				{
					return false;
				}
				using (PendingFallbackTransition transition = s.Inner.Close())
				{
					transition.Save(session);
				}
				return true;

			default:
				throw new NotSupportedException($"Unknown payjoin session state {state.GetType().Name}.");
		}
	}

	private async Task<bool> PollForOriginalAsync(PayjoinSession session, Initialized initialized, CancellationToken cancel)
	{
		using RequestResponse request = initialized.CreatePollRequest(RelayFor(session));
		byte[] body = await PayjoinTransport.PostAsync(_httpClientFactory, ClientName(session), request.Request, cancel).ConfigureAwait(false);
		using InitializedTransition transition = initialized.ProcessResponse(body, request.ClientResponse);
		using InitializedTransitionOutcome outcome = transition.Save(session);
		if (outcome is InitializedTransitionOutcome.Progress)
		{
			Logger.LogInfo($"Payjoin session {session.Id}: a sender posted their transaction.");
			return true;
		}

		return false;
	}

	/// <summary>
	/// A sender who wants to learn which coins are ours posts a made-up original and reads the
	/// coin we add (BIP 78). With a node we ask it whether the original would be accepted. Without
	/// one the check is not possible; every URI is single use and handed out by the user, so each
	/// probe costs the attacker a payment request and reveals at most one coin that was not private.
	/// </summary>
	private async Task CheckOriginalAsync(PayjoinSession session, Wallet wallet, UncheckedOriginalPayload payload, CancellationToken cancel)
	{
		// Kept for the day the session expires with this signed payment to us still unbroadcast.
		session.FallbackTx = Transaction.Load(payload.ExtractTxToCheckBroadcastSuitability(), _network);
		session.Persist();

		bool acceptable = true;
		if (_rpcClient is { } rpcClient)
		{
			try
			{
				MempoolAcceptResult result = await rpcClient.TestMempoolAcceptAsync(session.FallbackTx, cancel).ConfigureAwait(false);

				// An original the node already holds was broadcast by the sender in the meantime: as acceptable as it
				// gets. How much the sender pays in fees is their business, not the node's default ceiling.
				acceptable = result.IsAllowed || result.RejectReason is "txn-already-in-mempool" or "txn-already-known" or "max-fee-exceeded";
				if (!acceptable)
				{
					session.Error = $"The node would not accept the sender's transaction: {result.RejectReason}";
					session.Persist();
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				// A node that is down leaves us where a user without a node is; the sender will not wait for it to come back.
				Logger.LogWarning($"Payjoin session {session.Id}: the node could not check the sender's transaction, going on without it. {ex.Message}");
			}
		}

		// An original that would never confirm would keep our coin locked up until the session expires.
		FeeRate? slowestEstimate = wallet.FeeRateEstimations?.Estimations.Values.Min();
		ulong? minFeeRateSatPerKwu = slowestEstimate is null ? null : (ulong)(slowestEstimate.FeePerK.Satoshi / 4);

		using UncheckedOriginalPayloadTransition transition = payload.ApplyBroadcastSuitability(minFeeRateSatPerKwu, acceptable);
		transition.Save(session).Dispose();
	}

	private void ContributeInput(PayjoinSession session, Wallet wallet, WantsInputs wantsInputs)
	{
		Transaction original = session.FallbackTx ?? throw new InvalidOperationException("The sender's original transaction is missing.");

		// The extra input must look like the sender's: same script type and same sequence number.
		ScriptType senderInputType = GetUniformInputType(original)
			?? throw new InvalidOperationException("The sender's inputs are not all P2WPKH or all P2TR key path spends, so no coin of ours blends in.");
		uint sequence = original.Inputs[0].Sequence;
		if (original.Inputs.Any(x => x.Sequence != sequence))
		{
			throw new InvalidOperationException("The sender's inputs carry different sequence numbers, so no coin of ours blends in.");
		}

		lock (_selectionLock)
		{
			// A retry keeps the coin it already reserved. Otherwise never spend a coin that reached
			// the anonymity target: it would be linked to the payer. Coins the user keeps out of
			// coinjoin stay out of this too.
			SmartCoin[] candidates = session.ReservedOutpoint is { } reserved
				? wallet.Coins.Where(x => x.Outpoint == reserved && !x.IsSpent()).ToArray()
				: wallet.Coins
					.Where(x => x.Confirmed && x.IsAvailable() && !x.IsBanned && !x.IsExcludedFromCoinJoin && !x.Transaction.IsImmature(wallet.FilterHeaderChain.TipHeight))
					.Where(x => !x.IsPrivate(wallet.AnonScoreTarget) && x.ScriptPubKey.IsScriptType(senderInputType))
					.Where(x => x.Amount > new FeeRate((decimal)MaxFeeRateSatPerVb).GetFee(ContributedInputVirtualSize))
					.ToArray();
			if (candidates.Length == 0)
			{
				throw new InvalidOperationException(session.ReservedOutpoint is null
					? "No non-private coin of the sender's input type is available to contribute."
					: "The reserved coin is gone.");
			}

			InputPair[] pairs = candidates.Select(x => ToInputPair(x, sequence)).ToArray();
			try
			{
				using InputPair selected = wantsInputs.TryPreservingPrivacy(pairs);
				SmartCoin coin = candidates.Single(x => x.Outpoint == ToOutPoint(selected.Outpoint()));

				using WantsInputs contributed = wantsInputs.ContributeInputs([selected]);
				using WantsInputsTransition transition = contributed.CommitInputs();

				// Reserve before committing: a crash in between leaves a reservation that the session end releases, never a coin spent twice.
				session.ReservedOutpoint = coin.Outpoint;
				coin.PayjoinInProgress = true;
				session.Persist();
				transition.Save(session).Dispose();
				Logger.LogInfo($"Payjoin session {session.Id}: contributing {coin.Outpoint}.");
			}
			finally
			{
				foreach (InputPair pair in pairs)
				{
					pair.Dispose();
				}
			}
		}
	}

	/// <summary>The one input type every sender input has, if any. Wasabi coins are P2WPKH or P2TR key path spends.</summary>
	internal static ScriptType? GetUniformInputType(Transaction tx)
	{
		if (tx.Inputs.Count == 0)
		{
			return null;
		}

		// P2WPKH: a DER signature and a compressed public key. P2TR key path: one Schnorr signature.
		static bool IsP2wpkhSpend(TxIn x) => x.ScriptSig.Length == 0 && x.WitScript.PushCount == 2 && x.WitScript[0].Length is >= 64 and <= 73 && x.WitScript[1].Length == 33;
		static bool IsTaprootKeySpend(TxIn x) => x.ScriptSig.Length == 0 && x.WitScript.PushCount == 1 && x.WitScript[0].Length is 64 or 65;

		if (tx.Inputs.All(IsP2wpkhSpend))
		{
			return ScriptType.P2WPKH;
		}

		if (tx.Inputs.All(IsTaprootKeySpend))
		{
			return ScriptType.Taproot;
		}

		return null;
	}

	private bool SignContribution(PayjoinSession session, Wallet wallet, ProvisionalProposal proposal)
	{
		if (!wallet.IsLoggedIn)
		{
			Logger.LogDebug($"Payjoin session {session.Id}: waiting for the wallet to be unlocked to sign.");
			return false;
		}

		if (session.ReservedOutpoint is not { } reserved || !wallet.Coins.TryGetByOutPoint(reserved, out SmartCoin? coin) || coin.IsSpent())
		{
			throw new InvalidOperationException("The reserved coin is gone.");
		}
		PSBT signed = PayjoinSigner.Sign(PSBT.Parse(proposal.PsbtToSign(), _network), _network, wallet.KeyManager, wallet.Password, [coin]);
		using ProvisionalProposalTransition transition = proposal.FinalizeSignedProposal(signed.ToBase64());
		transition.Save(session).Dispose();
		return true;
	}

	private async Task PostProposalAsync(PayjoinSession session, PayjoinProposal proposal, CancellationToken cancel)
	{
		// Every input is segwit (ours by construction, the sender's by ContributeInput), so signing does not change the txid.
		session.ProposalTxid = PSBT.Parse(proposal.Psbt(), _network).GetGlobalTransaction().GetHash();
		session.Persist();

		using RequestResponse request = proposal.CreatePostRequest(RelayFor(session));
		byte[] body = await PayjoinTransport.PostAsync(_httpClientFactory, ClientName(session), request.Request, cancel).ConfigureAwait(false);
		using PayjoinProposalTransition transition = proposal.ProcessResponse(body, request.ClientResponse);
		transition.Save(session).Dispose();
		Logger.LogInfo($"Payjoin session {session.Id}: proposal sent to the directory.");
	}

	private async Task<bool> BroadcastFallbackAsync(PayjoinSession session, Transaction fallbackTx, CancellationToken cancel)
	{
		if (_fallbackRetryAfter.TryGetValue(session.Id, out DateTimeOffset retryAfter) && retryAfter > DateTimeOffset.UtcNow)
		{
			return false;
		}

		try
		{
			var tx = new SmartTransaction(fallbackTx, Mempool);
			await _broadcaster.SendTransactionAsync(tx, cancel).ConfigureAwait(false);
			Logger.LogInfo($"Payjoin session {session.Id}: fallback transaction {tx.GetHash()} broadcast.");
			return true;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			_fallbackRetryAfter[session.Id] = DateTimeOffset.UtcNow + FallbackRetryInterval;
			Logger.LogWarning($"Payjoin session {session.Id}: the fallback transaction could not be broadcast, the sender can still do it. {ex.Message}");
			return false;
		}
	}

	/// <summary>Gives the session up from whatever state it is in; the replay then yields the fallback to broadcast or the closed log.</summary>
	private static bool Cancel(PayjoinSession session, ReceiveSession state)
	{
		CancelTransition? transition = state switch
		{
			ReceiveSession.Initialized s => s.Inner.Cancel(),
			ReceiveSession.UncheckedOriginalPayload s => s.Inner.Cancel(),
			ReceiveSession.MaybeInputsOwned s => s.Inner.Cancel(),
			ReceiveSession.MaybeInputsSeen s => s.Inner.Cancel(),
			ReceiveSession.OutputsUnknown s => s.Inner.Cancel(),
			ReceiveSession.WantsOutputs s => s.Inner.Cancel(),
			ReceiveSession.WantsInputs s => s.Inner.Cancel(),
			ReceiveSession.WantsFeeRange s => s.Inner.Cancel(),
			ReceiveSession.ProvisionalProposal s => s.Inner.Cancel(),
			ReceiveSession.PayjoinProposal s => s.Inner.Cancel(),
			ReceiveSession.Monitor s => s.Inner.Cancel(),
			ReceiveSession.HasReplyableError s => s.Inner.Cancel(),
			_ => null,
		};
		if (transition is null)
		{
			return false;
		}

		using (transition)
		{
			transition.Save(session)?.Dispose();
		}

		return true;
	}

	private void Finish(PayjoinSession session, Wallet[] wallets, string? error)
	{
		// Unless another open session holds the same coin, which cannot happen unless the flag was lost and found again.
		if (session.ReservedOutpoint is { } reserved && Store.GetOpen().All(x => x.Id == session.Id || x.ReservedOutpoint != reserved))
		{
			SetReservation(session, wallets, reserved: false);
		}

		if (session.ReservedOutpoint is null)
		{
			// Nothing of ours was revealed, so the sender may offer the same inputs to a new session.
			Store.ReleaseInputsSeen(session.Id);
		}

		session.Error ??= error;
		session.Close();
		_relayFailures.TryRemove(session.Id, out _);
		_fallbackRetryAfter.TryRemove(session.Id, out _);
		Logger.LogInfo($"Payjoin session {session.Id} closed{(session.Error is null ? "" : $": {session.Error}")}");
	}

	/// <summary>The coin is looked up by outpoint on every loaded wallet, so a renamed wallet keeps its reservation.</summary>
	private static void SetReservation(PayjoinSession session, IEnumerable<Wallet> wallets, bool reserved)
	{
		if (session.ReservedOutpoint is not { } outpoint)
		{
			return;
		}

		foreach (Wallet wallet in wallets.Where(x => x.Loaded))
		{
			if (wallet.Coins.TryGetByOutPoint(outpoint, out SmartCoin? coin))
			{
				coin.PayjoinInProgress = reserved;
			}
		}
	}

	/// <summary>Whether the payment is settled one way or the other: the payjoin or the sender's original is known to the wallet.</summary>
	private bool IsCompleted(PayjoinSession session) =>
		(session.ProposalTxid is { } txid && _transactionStore.TryGetTransaction(txid, out _))
		|| (session.FallbackTx is { } fallbackTx && _transactionStore.TryGetTransaction(fallbackTx.GetHash(), out _));

	/// <summary>
	/// Our signed input sits in a proposal the sender still holds, so the coin stays reserved while the
	/// original is not on the network. Not forever, though: a sender who spent their inputs elsewhere
	/// has made the proposal worthless too, and the user can always give the session up.
	/// </summary>
	private static bool KeepCoinWhileFallbackIsOut(PayjoinSession session)
	{
		if (session.ProposalTxid is null || session.CancelRequested)
		{
			return false;
		}

		if (session.FallbackFailedSince is null)
		{
			session.FallbackFailedSince = DateTimeOffset.UtcNow;
			session.Persist();
		}

		return DateTimeOffset.UtcNow - session.FallbackFailedSince < FallbackHoldTime;
	}

	public override void Dispose()
	{
		_walletLoadedSubscription.Dispose();
		base.Dispose();
	}

	/// <summary>One relay per session, derived from its id; a transport failure moves it on to the next one.</summary>
	private string RelayFor(PayjoinSession session)
	{
		int seed = session.Id.Length >= 7 && int.TryParse(session.Id.AsSpan(0, 7), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int parsed) ? parsed : session.Id.Length;
		return _configuration.GetRelay(seed + _relayFailures.GetValueOrDefault(session.Id)).ToString();
	}

	private static string ClientName(PayjoinSession session) => $"payjoin-{session.Id}";

	private static OutPoint ToOutPoint(global::Payjoin.OutPoint outpoint) => new(uint256.Parse(outpoint.Txid), outpoint.Vout);

	private static InputPair ToInputPair(SmartCoin coin, uint sequence)
	{
		var txin = new global::Payjoin.TxIn(new global::Payjoin.OutPoint(coin.TransactionId.ToString(), coin.Index), [], sequence, []);
		var witnessUtxo = new global::Payjoin.TxOut((ulong)coin.Amount.Satoshi, coin.ScriptPubKey.ToBytes());

		// The library predicts the weight of a P2WPKH input itself but cannot for an unsigned P2TR
		// input: a key path spend with the default sighash is 230 weight units.
		Weight? expectedWeight = coin.ScriptPubKey.IsScriptType(ScriptType.Taproot) ? new Weight(230) : null;
		return new InputPair(txin, new PsbtInput(witnessUtxo, null, null), expectedWeight);
	}

	private sealed class OutPointCallback(Func<OutPoint, bool> check) : IsInputOwned, IsOutputKnown
	{
		public bool Callback(global::Payjoin.OutPoint outpoint) => check(ToOutPoint(outpoint));
	}

	private sealed class ScriptCallback(Func<Script, bool> check) : IsScriptOwned
	{
		public bool Callback(byte[] script) => check(Script.FromBytesUnsafe(script));
	}

	private sealed class TransactionFinderCallback(Func<uint256, Transaction?> find) : TransactionFinder
	{
		public byte[]? Callback(string txid) => find(uint256.Parse(txid))?.ToBytes();
	}
}
