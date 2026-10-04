using System.Net;
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
		return Read("info", () => new KruxInfo(
			new HDFingerprint(Convert.FromHexString(root.GetProperty("fingerprint").GetString()!)),
			root.GetProperty("rounds_used").GetInt32(),
			root.GetProperty("max_rounds").GetInt32(),
			root.TryGetProperty("authorized", out var authorized) ? authorized.GetBoolean() : null,
			root.TryGetProperty("device", out var device) ? device.GetString() ?? "krux" : "krux",
			// Names this wallet does not know are left out, never guessed at: a type the device does not sign would
			// have the client register inputs the device then refuses.
			root.TryGetProperty("script_types", out var types)
				? types.EnumerateArray().Select(t => t.GetString() switch { "p2wpkh" => ScriptType.P2WPKH, "p2tr" => ScriptType.Taproot, _ => (ScriptType?)null }).OfType<ScriptType>().ToArray()
				: [ScriptType.P2WPKH, ScriptType.Taproot]));
	}

	/// <summary>An account xpub with the fingerprint of the seed it came from, as the device computed both. Prompts on the device.</summary>
	public async Task<(HDFingerprint Fingerprint, ExtPubKey ExtPubKey)> GetXpubAsync(KeyPath accountKeyPath, CancellationToken cancellationToken)
	{
		string body = JsonSerializer.Serialize(new { path = accountKeyPath.Indexes });
		using var json = await PostAsync("xpub", body, Wallets.HardwareWalletService.AuthorizationTimeout, cancellationToken).ConfigureAwait(false);

		// The device answers in its own network's prefix (xpub or tpub); only the key material matters.
		return Read("xpub", () => (
			new HDFingerprint(Convert.FromHexString(json.RootElement.GetProperty("fingerprint").GetString()!)),
			new ExtPubKey(Encoders.Base58Check.DecodeData(json.RootElement.GetProperty("xpub").GetString()!)[4..])));
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
		return Read("proof", () => Convert.FromHexString(json.RootElement.GetProperty("proof").GetString()!));
	}

	/// <summary>Sends the round PSBT; the device validates it against its on-device policy and signs. Slow over a Krux's UART, hence the signing timeout.</summary>
	public async Task<PSBT> SignCoinJoinAsync(PSBT psbt, Network network, CancellationToken cancellationToken)
	{
		string body = JsonSerializer.Serialize(new { psbt = psbt.ToBase64() });
		using var json = await PostAsync("sign", body, Wallets.HardwareWalletService.SigningTimeout(0), cancellationToken).ConfigureAwait(false);
		return Read("sign", () => PSBT.Parse(json.RootElement.GetProperty("psbt").GetString()!, network));
	}

	/// <summary>
	/// The bridge is untrusted input: an answer without the documented fields, or with values that do not parse, is
	/// a bridge failure like any other rather than whatever the parser happened to throw - which callers that try
	/// one vendor after another would otherwise not recognise, and stop at.
	/// </summary>
	private static T Read<T>(string endpoint, Func<T> parse)
	{
		try
		{
			return parse();
		}
		catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
		{
			throw new KruxException($"kruxd answered '{endpoint}' with something that cannot be read. ({e.Message})");
		}
	}

	private async Task<JsonDocument> PostAsync(string endpoint, string body, TimeSpan timeout, CancellationToken cancellationToken)
	{
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutCts.CancelAfter(timeout);
		using var content = new StringContent(body, Encoding.UTF8, "application/json");

		HttpStatusCode status;
		string responseBody;
		try
		{
			using var response = await _httpClient.PostAsync($"{_bridgeUri}/{endpoint}", content, timeoutCts.Token).ConfigureAwait(false);
			status = response.StatusCode;
			responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
		}
		catch (HttpRequestException e)
		{
			throw new Wallets.HardwareWalletTransportNotFoundException($"kruxd is not running at {_bridgeUri}. Start it and approve the signing session on the device. ({e.Message})");
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new KruxException($"kruxd did not answer '{endpoint}' within {timeout.TotalSeconds:0} seconds.");
		}

		if (status != HttpStatusCode.OK)
		{
			// The device's own reason when kruxd gives one; otherwise whatever answered, status and all.
			throw new KruxException($"kruxd answered '{endpoint}' with {(int)status}: {ErrorOf(responseBody)}");
		}

		// Anything may sit on the port; what does not even speak JSON is not kruxd.
		return Read(endpoint, () => JsonDocument.Parse(responseBody));
	}

	private static string ErrorOf(string responseBody)
	{
		try
		{
			using var json = JsonDocument.Parse(responseBody);
			return json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString()! : responseBody;
		}
		catch (JsonException)
		{
			return responseBody;
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
