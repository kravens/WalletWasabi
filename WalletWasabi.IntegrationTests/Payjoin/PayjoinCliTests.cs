using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.RPC;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionBroadcasting;
using WalletWasabi.Blockchain.TransactionBuilding;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.IntegrationTests.Infrastructure;
using WalletWasabi.Payjoin;
using WalletWasabi.Wallets;
using Xunit;

namespace WalletWasabi.IntegrationTests.Payjoin;

/// <summary>
/// Round trips against payjoin-cli, the Payjoin Dev Kit reference client, in both directions.
/// They need a BIP 77 directory with an OHTTP relay (payjoin-test-utils' TestServices: a TLS
/// directory with a self-signed certificate and plain-HTTP relays that trust it) and payjoin-cli
/// built with its <c>_manual-tls</c> feature. Both come from environment variables and the tests
/// are skipped without them:
/// <c>PAYJOIN_CLI_BIN</c>, <c>PAYJOIN_DIRECTORY_URL</c>, <c>PAYJOIN_OHTTP_RELAY_URL</c>,
/// <c>PAYJOIN_ROOT_CERT_DER</c> (path to the directory's certificate).
/// </summary>
[Collection("Integration tests")]
public class PayjoinCliTests(IntegrationTestFixture fixture)
{
	private static readonly Money ReceiverCoin = Money.Coins(1m);
	private static readonly Money Payment = Money.Coins(0.3m);

	[Theory(Timeout = 240_000)]
	[InlineData(ScriptPubKeyType.Segwit)]
	[InlineData(ScriptPubKeyType.TaprootBIP86)]
	public async Task WasabiReceivesPayjoinFromPayjoinCliAsync(ScriptPubKeyType scriptType)
	{
		Harness harness = Harness.FromEnvironment();
		CancellationToken ct = TestContext.Current.CancellationToken;
		await using RegTestEnvironment env = await RegTestEnvironment.CreateAsync(fixture);
		(Wallet wallet, SmartCoin coin) = await StartFundedWalletAsync(env, scriptType, ct);
		try
		{
			var broadcaster = new TransactionBroadcaster([new RpcBroadcaster(env.RpcClient)], env.MempoolService);
			// With a node the original is checked against its mempool; without one the receiver is assumed interactive. Cover both.
			PayjoinReceiverManager CreateManager() =>
				new(Path.Combine(env.WorkDir, "Payjoin"), env.Network, harness.Configuration, () => Task.FromResult<IEnumerable<Wallet>>([wallet]), harness.HttpClientFactory, broadcaster, env.TransactionStore, scriptType == ScriptPubKeyType.Segwit ? env.RpcClient : null, env.EventBus);

			HdPubKey receiveKey = wallet.KeyManager.GetNextReceiveKey(new LabelsArray("payjoin"), scriptType);
			PayjoinSession session;
			using (PayjoinReceiverManager firstRun = CreateManager())
			{
				await firstRun.StartAsync(ct);
				session = await firstRun.StartSessionAsync(wallet, () => receiveKey, Payment, ct);
				await firstRun.StopAsync(ct);
			}

			Assert.Contains("pj=", session.PjUri, StringComparison.OrdinalIgnoreCase);

			// A restart must pick the session up from its persisted event log.
			using PayjoinReceiverManager manager = CreateManager();
			await manager.StartAsync(ct);

			(string rpcHost, string[] auth) = await CreateCliWalletAsync(env, $"pjsender-{scriptType}", Money.Coins(1m), scriptType);
			using CliProcess sender = CliProcess.Start(harness, rpcHost, auth, Path.Combine(env.WorkDir, $"pjsender-{scriptType}.db"), "send", session.PjUri!, "--fee-rate", "2");
			string sentLine = await sender.WaitForLineAsync(x => x.Contains("Payjoin sent. TXID:"), TimeSpan.FromSeconds(150), ct);
			uint256 txid = uint256.Parse(sentLine.Split(':').Last().Trim());

			await env.RpcClient.GenerateAsync(1, ct);
			await env.SyncFiltersRpcAsync(ct);
			await env.WaitForConditionAsync(() => wallet.TransactionStore.TryGetTransaction(txid, out _), TimeSpan.FromSeconds(30));
			await manager.TriggerAndWaitRoundAsync(ct);

			Assert.True(wallet.TransactionStore.TryGetTransaction(txid, out SmartTransaction? payjoinTx));
			Assert.Equal(2, payjoinTx!.Transaction.Inputs.Count);
			Assert.Contains(payjoinTx.Transaction.Inputs, x => x.PrevOut == coin.Outpoint);
			// Our contribution has the sender's input type and sequence, so the extra input does not give the payjoin away.
			Assert.All(payjoinTx.Transaction.Inputs, x => Assert.Equal(scriptType == ScriptPubKeyType.TaprootBIP86 ? 1 : 2, x.WitScript.PushCount));
			Assert.All(payjoinTx.Transaction.Inputs, x => Assert.Equal(payjoinTx.Transaction.Inputs[0].Sequence, x.Sequence));
			TxOut ourOutput = Assert.Single(payjoinTx.Transaction.Outputs, x => x.ScriptPubKey == receiveKey.GetAssumedScriptPubKey());
			Assert.InRange(ourOutput.Value, Payment + ReceiverCoin - Money.Coins(0.001m), Payment + ReceiverCoin);

			PayjoinSession closed = Assert.Single(manager.Store.GetAll());
			Assert.True(closed.IsClosed);
			Assert.Equal(txid, closed.ProposalTxid);
			Assert.Null(closed.Error);
			Assert.False(coin.PayjoinInProgress);
			Assert.True(coin.IsSpent());
		}
		finally
		{
			await wallet.StopAsync(CancellationToken.None);
		}
	}

	[Theory(Timeout = 240_000)]
	[InlineData(ScriptPubKeyType.Segwit)]
	[InlineData(ScriptPubKeyType.TaprootBIP86)]
	public async Task WasabiSendsPayjoinToPayjoinCliAsync(ScriptPubKeyType scriptType)
	{
		Harness harness = Harness.FromEnvironment();
		CancellationToken ct = TestContext.Current.CancellationToken;
		await using RegTestEnvironment env = await RegTestEnvironment.CreateAsync(fixture);
		(Wallet wallet, SmartCoin coin) = await StartFundedWalletAsync(env, scriptType, ct);
		try
		{
			(string rpcHost, string[] auth) = await CreateCliWalletAsync(env, $"pjreceiver-{scriptType}", ReceiverCoin, scriptType);
			string db = Path.Combine(env.WorkDir, $"pjreceiver-{scriptType}.db");
			string uri;
			using (CliProcess receive = CliProcess.Start(harness, rpcHost, auth, db, "receive", Payment.Satoshi.ToString(), "--max-fee-rate", "50", "--pj-directories", harness.Configuration.DirectoryUri.AbsoluteUri.TrimEnd('/')))
			{
				uri = await receive.WaitForLineAsync(x => x.StartsWith("bitcoin:", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(60), ct);
			}

			// payjoin-cli answers pending sessions from its database when resumed.
			using CliProcess resume = CliProcess.Start(harness, rpcHost, auth, db, "resume");

			using var pjUri = global::Payjoin.Uri.Parse(uri);
			BitcoinAddress receiverAddress = BitcoinAddress.Create(pjUri.Address(), env.Network);
			BuildTransactionResult original = PayjoinSender.BuildOriginal(wallet, RegTestEnvironment.DefaultPassword, new PaymentIntent(receiverAddress.ScriptPubKey, Payment, label: new LabelsArray("payjoin")), new FeeRate(2m), allowedInputs: null);

			// The original must be final for the receiver's node right away; Wasabi's usual lock time may point past the tip.
			LockTime lockTime = original.Transaction.Transaction.LockTime;
			Assert.True(lockTime == LockTime.Zero || (lockTime.IsHeightLock && lockTime.Height < wallet.FilterHeaderChain.TipHeight.Height), lockTime.ToString());

			PayjoinSendResult result = await PayjoinSender.SendAsync(uri, original, env.Network, wallet.KeyManager, RegTestEnvironment.DefaultPassword, harness.Configuration, harness.HttpClientFactory, TimeSpan.FromSeconds(150), ct);
			Assert.True(result.IsPayjoin, result.FallbackReason);
			await resume.WaitForLineAsync(x => x.Contains("Response successful"), TimeSpan.FromSeconds(30), ct);

			var broadcaster = new TransactionBroadcaster([new RpcBroadcaster(env.RpcClient)], env.MempoolService);
			await broadcaster.SendTransactionAsync(result.Transaction, ct);
			Assert.Contains(result.Transaction.GetHash(), await env.RpcClient.GetRawMempoolAsync());

			Transaction payjoinTx = result.Transaction.Transaction;
			Assert.Equal(2, payjoinTx.Inputs.Count);
			Assert.Contains(payjoinTx.Inputs, x => x.PrevOut == coin.Outpoint);
			TxOut receiverOutput = Assert.Single(payjoinTx.Outputs, x => x.ScriptPubKey == receiverAddress.ScriptPubKey);
			Assert.True(receiverOutput.Value > Payment, "The receiver did not add an input.");
			Assert.Equal(original.Transaction.Transaction.Outputs.Count, payjoinTx.Outputs.Count);
		}
		finally
		{
			await wallet.StopAsync(CancellationToken.None);
		}
	}

	[Fact(Timeout = 240_000)]
	public async Task CancelGivesTheSessionUpAsync()
	{
		Harness harness = Harness.FromEnvironment();
		CancellationToken ct = TestContext.Current.CancellationToken;
		await using RegTestEnvironment env = await RegTestEnvironment.CreateAsync(fixture);
		(Wallet wallet, _) = await StartFundedWalletAsync(env, ScriptPubKeyType.Segwit, ct);
		try
		{
			var broadcaster = new TransactionBroadcaster([new RpcBroadcaster(env.RpcClient)], env.MempoolService);
			using var manager = new PayjoinReceiverManager(Path.Combine(env.WorkDir, "Payjoin"), env.Network, harness.Configuration, () => Task.FromResult<IEnumerable<Wallet>>([wallet]), harness.HttpClientFactory, broadcaster, env.TransactionStore, null, env.EventBus);
			await manager.StartAsync(ct);
			PayjoinSession session = await manager.StartSessionAsync(wallet, () => wallet.KeyManager.GetNextReceiveKey(new LabelsArray("payjoin")), null, ct);

			Assert.Throws<InvalidOperationException>(() => manager.RequestCancel(session.Id, "another wallet"));
			manager.RequestCancel(session.Id, wallet.WalletName);
			await env.WaitForConditionAsync(() => manager.Store.GetAll().Single().IsClosed, TimeSpan.FromSeconds(60));

			PayjoinSession closed = Assert.Single(manager.Store.GetAll());
			Assert.Equal("Cancelled.", closed.Error);
			Assert.Empty(manager.Store.GetOpen());
		}
		finally
		{
			await wallet.StopAsync(CancellationToken.None);
		}
	}

	private static async Task<(Wallet Wallet, SmartCoin Coin)> StartFundedWalletAsync(RegTestEnvironment env, ScriptPubKeyType scriptType, CancellationToken ct)
	{
		KeyManager keyManager = env.CreateKeyManager();
		Wallet wallet = env.CreateWallet(keyManager);
		Assert.True(wallet.TryLogin(RegTestEnvironment.DefaultPassword, out _));

		BitcoinAddress fundingAddress = keyManager.GetNextReceiveKey(new LabelsArray("funding"), scriptType).GetAddress(env.Network);
		await env.FundAddressAsync(fundingAddress, ReceiverCoin, confirmations: 1);
		await env.SyncFiltersRpcAsync(ct);
		await wallet.StartAsync(ct);
		await env.WaitForConditionAsync(() => wallet.Coins.Any(x => x.Confirmed), TimeSpan.FromSeconds(30));
		return (wallet, wallet.Coins.Single());
	}

	/// <summary>A funded Bitcoin Core wallet for payjoin-cli on the shared regtest node, plus the arguments payjoin-cli needs to reach it.</summary>
	private async Task<(string RpcHost, string[] Auth)> CreateCliWalletAsync(RegTestEnvironment env, string name, Money funding, ScriptPubKeyType scriptType)
	{
		RPCClient rpc = await fixture.BitcoinCoreNode.RpcClient.CreateWalletAsync(name);
		RPCResponse newAddress = await rpc.SendCommandAsync("getnewaddress", "", scriptType == ScriptPubKeyType.TaprootBIP86 ? "bech32m" : "bech32");
		await env.FundAddressAsync(BitcoinAddress.Create(newAddress.Result.ToString(), env.Network), funding, confirmations: 1);

		RPCCredentialString credentials = fixture.BitcoinCoreNode.RpcClient.CredentialString;
		string[] auth = credentials.CookieFile is { } cookie
			? ["--cookie-file", cookie]
			: ["--rpcuser", credentials.UserPassword.UserName, "--rpcpassword", credentials.UserPassword.Password];
		return ($"http://{fixture.BitcoinCoreNode.RpcEndPoint}/wallet/{name}", auth);
	}

	private sealed record Harness(string PayjoinCli, string CertificatePath, PayjoinConfiguration Configuration, IHttpClientFactory HttpClientFactory)
	{
		public static Harness FromEnvironment()
		{
			string? cli = Environment.GetEnvironmentVariable("PAYJOIN_CLI_BIN");
			string? directory = Environment.GetEnvironmentVariable("PAYJOIN_DIRECTORY_URL");
			string? relay = Environment.GetEnvironmentVariable("PAYJOIN_OHTTP_RELAY_URL");
			string? certificate = Environment.GetEnvironmentVariable("PAYJOIN_ROOT_CERT_DER");
			if (cli is null || directory is null || relay is null || certificate is null)
			{
				Assert.Skip("Set PAYJOIN_CLI_BIN, PAYJOIN_DIRECTORY_URL, PAYJOIN_OHTTP_RELAY_URL and PAYJOIN_ROOT_CERT_DER to run the payjoin-cli round trips.");
			}

			// UseTor: the directory is reached directly by the (certificate-pinned) client, as it would be over Tor.
			var configuration = new PayjoinConfiguration(new Uri(directory), [new Uri(relay)], new FeeRate(50m), UseTor: true);
			return new Harness(cli, certificate, configuration, new PinnedCertificateHttpClientFactory(File.ReadAllBytes(certificate)));
		}
	}

	private sealed class PinnedCertificateHttpClientFactory(byte[] certificate) : IHttpClientFactory
	{
		public HttpClient CreateClient(string name)
		{
#pragma warning disable CA2000 // Dispose objects before losing scope - handler ownership transferred to HttpClient
			var handler = new HttpClientHandler
			{
				UseProxy = false,
				ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null && cert.RawData.AsSpan().SequenceEqual(certificate),
			};
			return new HttpClient(handler, disposeHandler: true);
#pragma warning restore CA2000
		}
	}

	private sealed class CliProcess : IDisposable
	{
		private readonly Process _process;
		private readonly StringBuilder _stderr = new();

		private CliProcess(Process process)
		{
			_process = process;
			_process.ErrorDataReceived += (_, e) =>
			{
				if (e.Data is { } line)
				{
					lock (_stderr)
					{
						_stderr.AppendLine(line);
					}
				}
			};
			_process.BeginErrorReadLine();
		}

		public static CliProcess Start(Harness harness, string rpcHost, string[] auth, string dbPath, params string[] command)
		{
			var startInfo = new ProcessStartInfo(harness.PayjoinCli)
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
			};
			string[] common = ["--root-certificate", harness.CertificatePath, "--rpchost", rpcHost, "--db-path", dbPath, "--ohttp-relays", harness.Configuration.OhttpRelays[0].AbsoluteUri.TrimEnd('/')];
			foreach (string argument in common.Concat(auth).Concat(command))
			{
				startInfo.ArgumentList.Add(argument);
			}

			return new CliProcess(Process.Start(startInfo) ?? throw new InvalidOperationException("payjoin-cli did not start."));
		}

		public async Task<string> WaitForLineAsync(Func<string, bool> match, TimeSpan timeout, CancellationToken cancellationToken)
		{
			using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			cts.CancelAfter(timeout);
			while (true)
			{
				string? line = await _process.StandardOutput.ReadLineAsync(cts.Token);
				if (line is null)
				{
					await _process.WaitForExitAsync(cts.Token);
					throw new InvalidOperationException($"payjoin-cli exited with {_process.ExitCode}. {_stderr}");
				}

				if (match(line))
				{
					return line;
				}
			}
		}

		public void Dispose()
		{
			if (!_process.HasExited)
			{
				_process.Kill(entireProcessTree: true);
				_process.WaitForExit();
			}

			_process.Dispose();
		}
	}
}
