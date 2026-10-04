using System.Net.Http;
using System.Text;
using System.Text.Json;
using NBitcoin.DataEncoders;

namespace WalletWasabi.Hwi.Krux;

/// <summary>
/// Talks to a Krux, or its SabiSigner fork, through the kruxd bridge (see coinjoin.nl/kruxd). The user
/// pre-approves one signing session on the device, after which ownership proofs and policy-checked round
/// signatures are produced without further interaction. The bridge is a plain local HTTP process and is
/// trusted with nothing the device does not vouch for: accounts it hands out are pinned to the fingerprint
/// of the authorized session, and every call is bounded so a hung bridge cannot hold the wallet forever.
/// </summary>
public class KruxClient : IDisposable
{
	/// <summary>kruxd default; 21325/21328 belong to the Trezor bridge.</summary>
	public const string DefaultBridgeUri = "http://127.0.0.1:21326";

#pragma warning disable CA2000 // Dispose objects before losing scope - the HttpClient owns the handler and disposes it.
	public KruxClient(string? bridgeUri = null, HttpMessageHandler? handler = null, TimeSpan? infoTimeout = null)
	{
		_bridgeUri = bridgeUri ?? DefaultBridgeUri;
		_infoTimeout = infoTimeout ?? TimeSpan.FromSeconds(10);
		_httpClient = new HttpClient(handler ?? new SocketsHttpHandler(), disposeHandler: true)
		{
			Timeout = Timeout.InfiniteTimeSpan // each call carries its own bound below
		};
	}
#pragma warning restore CA2000

	private readonly string _bridgeUri;
	private readonly TimeSpan _infoTimeout;
	private readonly HttpClient _httpClient;

	/// <param name="Authorized">Whether the device reported an approved session; null when the bridge did not say.</param>
	/// <param name="ScriptTypes">What the device can sign in a round; an older bridge that does not say gets both.</param>
	public record KruxInfo(HDFingerprint Fingerprint, int RoundsUsed, int MaxRounds, bool? Authorized, string Device, IReadOnlyList<ScriptType> ScriptTypes);

	public async Task<KruxInfo> GetInfoAsync(CancellationToken cancellationToken)
	{
		using var json = await PostAsync("info", "{}", _infoTimeout, cancellationToken).ConfigureAwait(false);
		var root = json.RootElement;
		return new KruxInfo(
			new HDFingerprint(Convert.FromHexString(root.GetProperty("fingerprint").GetString()!)),
			root.GetProperty("rounds_used").GetInt32(),
			root.GetProperty("max_rounds").GetInt32(),
			root.TryGetProperty("authorized", out var authorized) ? authorized.GetBoolean() : null,
			root.TryGetProperty("device", out var device) ? device.GetString() ?? "krux" : "krux",
			root.TryGetProperty("script_types", out var types)
				? types.EnumerateArray().Select(t => t.GetString() == "p2tr" ? ScriptType.Taproot : ScriptType.P2WPKH).ToArray()
				: [ScriptType.P2WPKH, ScriptType.Taproot]);
	}

	/// <summary>An account xpub with the fingerprint of the seed it came from, as the device computed both. Prompts on the device.</summary>
	public async Task<(HDFingerprint Fingerprint, ExtPubKey ExtPubKey)> GetXpubAsync(KeyPath accountKeyPath, CancellationToken cancellationToken)
	{
		string body = JsonSerializer.Serialize(new { path = accountKeyPath.Indexes });
		using var json = await PostAsync("xpub", body, Wallets.HardwareWalletService.AuthorizationTimeout, cancellationToken).ConfigureAwait(false);

		// The device answers in its own network's prefix (xpub or tpub); only the key material matters.
		var extPubKey = new ExtPubKey(Encoders.Base58Check.DecodeData(json.RootElement.GetProperty("xpub").GetString()!)[4..]);
		return (new HDFingerprint(Convert.FromHexString(json.RootElement.GetProperty("fingerprint").GetString()!)), extPubKey);
	}

	public async Task<byte[]> GetOwnershipProofAsync(KeyPath keyPath, ScriptPubKeyType scriptType, byte[] commitmentData, CancellationToken cancellationToken)
	{
		string scriptTypeName = scriptType == ScriptPubKeyType.TaprootBIP86 ? "p2tr" : "p2wpkh";
		string body = JsonSerializer.Serialize(new
		{
			script_type = scriptTypeName,
			path = keyPath.Indexes,
			commitment = Convert.ToHexString(commitmentData).ToLowerInvariant()
		});
		using var json = await PostAsync("proof", body, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
		return Convert.FromHexString(json.RootElement.GetProperty("proof").GetString()!);
	}

	/// <summary>Sends the round PSBT; the device validates it against its on-device policy and signs. Slow over a Krux's UART, hence the signing timeout.</summary>
	public async Task<PSBT> SignCoinJoinAsync(PSBT psbt, Network network, CancellationToken cancellationToken)
	{
		string body = JsonSerializer.Serialize(new { psbt = psbt.ToBase64() });
		using var json = await PostAsync("sign", body, Wallets.HardwareWalletService.SigningTimeout(0), cancellationToken).ConfigureAwait(false);
		return PSBT.Parse(json.RootElement.GetProperty("psbt").GetString()!, network);
	}

	private async Task<JsonDocument> PostAsync(string endpoint, string body, TimeSpan timeout, CancellationToken cancellationToken)
	{
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutCts.CancelAfter(timeout);
		using var content = new StringContent(body, Encoding.UTF8, "application/json");

		HttpResponseMessage response;
		try
		{
			response = await _httpClient.PostAsync($"{_bridgeUri}/{endpoint}", content, timeoutCts.Token).ConfigureAwait(false);
		}
		catch (HttpRequestException e)
		{
			throw new Wallets.HardwareWalletTransportNotFoundException($"kruxd is not running at {_bridgeUri}. Start it and approve the signing session on the device. ({e.Message})");
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new KruxException($"kruxd did not answer '{endpoint}' within {timeout.TotalSeconds:0} seconds.");
		}

		using (response)
		{
			string responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
			var json = JsonDocument.Parse(responseBody);
			if (!response.IsSuccessStatusCode)
			{
				string error = json.RootElement.TryGetProperty("error", out var errorElement)
					? errorElement.GetString() ?? "unknown"
					: responseBody;
				json.Dispose();
				throw new KruxException(error);
			}
			return json;
		}
	}

	public void Dispose() => _httpClient.Dispose();
}

/// <summary>The device or bridge refused a request (policy violation, exhausted round budget, ...).</summary>
public class KruxException : Wallets.HardwareWalletException
{
	public KruxException(string message) : base(message)
	{
	}
}
