using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;

namespace WalletWasabi.Hwi.Krux;

/// <summary>
/// Talks to a Krux device through the kruxd bridge (serial/TCP to HTTP, see coinjoin.nl/kruxd).
/// The device must be on its "CoinJoin USB" screen: the user pre-approves one signing session
/// there (policy summary + hold-to-confirm equivalent), after which ownership proofs and
/// policy-checked round signatures are produced without further interaction.
/// </summary>
public class KruxClient : IDisposable
{
	/// <summary>kruxd default; 21325/21328 belong to the Trezor bridge.</summary>
	public const string DefaultBridgeUri = "http://127.0.0.1:21326";

	public KruxClient(string? bridgeUri = null)
	{
		// The bridge listens on localhost only, no clearnet traffic is involved.
		_bridgeUri = bridgeUri ?? DefaultBridgeUri;
		SocketsHttpHandler? handler = new();
		try
		{
			_httpClient = new HttpClient(handler, disposeHandler: true)
			{
				// Signing a big round over a 115200 baud UART takes a while, do not time it out here.
				Timeout = Timeout.InfiniteTimeSpan
			};
			handler = null;
		}
		finally
		{
			handler?.Dispose();
		}
	}

	private readonly string _bridgeUri;
	private readonly HttpClient _httpClient;

	public record KruxInfo(HDFingerprint Fingerprint, int RoundsUsed, int MaxRounds);

	public async Task<KruxInfo> GetInfoAsync(CancellationToken cancellationToken)
	{
		using var json = await PostAsync("info", "{}", cancellationToken).ConfigureAwait(false);
		return new KruxInfo(
			new HDFingerprint(Convert.FromHexString(json.RootElement.GetProperty("fingerprint").GetString()!)),
			json.RootElement.GetProperty("rounds_used").GetInt32(),
			json.RootElement.GetProperty("max_rounds").GetInt32());
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
		using var json = await PostAsync("proof", body, cancellationToken).ConfigureAwait(false);
		return Convert.FromHexString(json.RootElement.GetProperty("proof").GetString()!);
	}

	/// <summary>Sends the round PSBT; the device validates it against its on-device policy and signs.</summary>
	public async Task<PSBT> SignCoinJoinAsync(PSBT psbt, Network network, CancellationToken cancellationToken)
	{
		string body = JsonSerializer.Serialize(new { psbt = psbt.ToBase64() });
		using var json = await PostAsync("sign", body, cancellationToken).ConfigureAwait(false);
		return PSBT.Parse(json.RootElement.GetProperty("psbt").GetString()!, network);
	}

	private async Task<JsonDocument> PostAsync(string endpoint, string body, CancellationToken cancellationToken)
	{
		using var content = new StringContent(body, Encoding.UTF8, "application/json");
		using var response = await _httpClient.PostAsync($"{_bridgeUri}/{endpoint}", content, cancellationToken).ConfigureAwait(false);
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

	public void Dispose() => _httpClient.Dispose();
}

/// <summary>Raised when the device or bridge rejects a request (policy violation, exhausted round budget, ...).</summary>
public class KruxException : Exception
{
	public KruxException(string message) : base(message)
	{
	}

	public bool IsRoundBudgetExhausted => Message.Contains("round budget exhausted");
}
