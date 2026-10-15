using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Blockchain.TransactionBuilding;
using static WalletWasabi.Models.Height;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Payjoin;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Payjoin;

public class PayjoinSenderTests
{
	private const string Password = "foo";
	private static readonly PayjoinConfiguration Configuration = PayjoinConfiguration.Create(PayjoinTestVectors.Directory, ["https://relay.example", "https://other-relay.example"], 50m, useTor: true);

	[Fact]
	public async Task FallsBackToTheOriginalWhenEveryRelayIsDownAsync()
	{
		var (factory, original, uri) = CreateOriginal();
		var relaysTried = new HashSet<string>();

		PayjoinSendResult result = await PayjoinSender.SendAsync(uri, original, Network.Main, factory.KeyManager, Password, Configuration, new FakeHttpClientFactory(request =>
		{
			relaysTried.Add(request.RequestUri!.Host);
			throw new HttpRequestException("relay down");
		}), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

		Assert.False(result.IsPayjoin);
		Assert.Same(original.Transaction, result.Transaction);
		Assert.Contains("relay down", result.FallbackReason);
		Assert.Equal(["other-relay.example", "relay.example"], relaysTried.Order());
	}

	[Fact]
	public async Task FallsBackToTheOriginalOnAnUnreadableAnswerAsync()
	{
		var (factory, original, uri) = CreateOriginal();
		using var garbage = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[8192]) };

		PayjoinSendResult result = await PayjoinSender.SendAsync(uri, original, Network.Main, factory.KeyManager, Password, Configuration, new FakeHttpClientFactory(_ => garbage), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

		Assert.False(result.IsPayjoin);
		Assert.Same(original.Transaction, result.Transaction);
		Assert.NotNull(result.FallbackReason);
	}

	[Fact]
	public async Task FallsBackToTheOriginalWithoutPayjoinEndpointAsync()
	{
		var (factory, original, _) = CreateOriginal();
		string plainUri = $"bitcoin:{original.Transaction.Transaction.Outputs[0].ScriptPubKey.GetDestinationAddress(Network.Main)}?amount=0.5";

		PayjoinSendResult result = await PayjoinSender.SendAsync(plainUri, original, Network.Main, factory.KeyManager, Password, Configuration, new FakeHttpClientFactory(_ => throw new InvalidOperationException("must not be called")), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

		Assert.False(result.IsPayjoin);
		Assert.Same(original.Transaction, result.Transaction);
	}

	[Fact]
	public async Task CancellationIsNotAFallbackAsync()
	{
		var (factory, original, uri) = CreateOriginal();
		using var cts = new CancellationTokenSource();
		await cts.CancelAsync();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			PayjoinSender.SendAsync(uri, original, Network.Main, factory.KeyManager, Password, Configuration, new FakeHttpClientFactory(_ => throw new HttpRequestException("relay down")), TimeSpan.FromSeconds(30), cts.Token));
	}

	[Fact]
	public async Task RefusesAnUnsignedOriginalAsync()
	{
		var (factory, _, uri) = CreateOriginal();
		BuildTransactionResult unsigned = CreateOriginal(factory, sign: false).Original;

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			PayjoinSender.SendAsync(uri, unsigned, Network.Main, factory.KeyManager, Password, Configuration, new FakeHttpClientFactory(_ => throw new InvalidOperationException("must not be called")), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
	}

	[Fact]
	public void LockTimeNeverReachesTheTip()
	{
		var tip = new ChainHeight(800_000);
		Assert.Equal(LockTime.Zero, PayjoinSender.ChooseLockTime(LockTime.Zero, tip));
		Assert.Equal(new LockTime(799_999), PayjoinSender.ChooseLockTime(new LockTime(799_999), tip));
		Assert.Equal(LockTime.Zero, PayjoinSender.ChooseLockTime(new LockTime(800_000), tip));
		Assert.Equal(LockTime.Zero, PayjoinSender.ChooseLockTime(new LockTime(800_001), tip));

		// On a chain shorter than the selector's reach it produces a time lock; that must not get through either.
		Assert.Equal(LockTime.Zero, PayjoinSender.ChooseLockTime(new LockTime(DateTimeOffset.UtcNow), new ChainHeight(5)));
	}

	/// <summary>A signed Wasabi transaction paying 0.5 BTC to a fresh key, plus the receiver's BIP 77 URI for that payment.</summary>
	private static (TransactionFactory Factory, BuildTransactionResult Original, string Uri) CreateOriginal() =>
		CreateOriginal(ServiceFactory.CreateTransactionFactory([("Pablo", 0, 1m, true, 1)]), sign: true);

	private static (TransactionFactory Factory, BuildTransactionResult Original, string Uri) CreateOriginal(TransactionFactory factory, bool sign)
	{
		using var destination = new Key();
		BitcoinAddress address = destination.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.Main);

		TransactionParameters parameters = TransactionParametersBuilder.CreateDefault()
			.SetPayment(new PaymentIntent(address.ScriptPubKey, Money.Coins(0.5m)))
			.SetFeeRate(2m)
			.SetTryToSign(sign)
			.Build();
		BuildTransactionResult original = factory.BuildTransaction(parameters);

		// The sender side of the library insists on a finalized original: that is what Wasabi builds.
		Assert.Equal(sign, original.Psbt.IsAllFinalized());

		string uri = PayjoinTestVectors.CreateReceiverUri(address.ToString(), 50_000_000, new PayjoinTestVectors.InMemoryReceiverPersister());
		return (factory, original, uri);
	}

	private sealed class FakeHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> respond) : IHttpClientFactory
	{
		public HttpClient CreateClient(string name)
		{
#pragma warning disable CA2000 // Dispose objects before losing scope - handler ownership transferred to HttpClient
			return new HttpClient(new FakeHandler(respond), disposeHandler: true);
#pragma warning restore CA2000
		}

		private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
		{
			protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
			{
				cancellationToken.ThrowIfCancellationRequested();
				return Task.FromResult(respond(request));
			}
		}
	}
}
