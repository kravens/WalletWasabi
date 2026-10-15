using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Payjoin;
using WalletWasabi.Logging;
using Uri = System.Uri;

namespace WalletWasabi.Payjoin;

/// <summary>
/// The only place payjoin bytes touch the network. Requests are OHTTP-encapsulated by the library,
/// so a relay sees who we are but not what we send and the directory sees the opposite.
/// </summary>
internal static class PayjoinTransport
{
	/// <summary>The directory holds an empty mailbox request open for up to 30 seconds.</summary>
	private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(45);

	/// <summary>Encapsulated messages are 8 KiB and key configurations a few hundred bytes; anything bigger is not for us.</summary>
	private const long MaxResponseBytes = 64 * 1024;

	public static async Task<byte[]> PostAsync(IHttpClientFactory httpClientFactory, string clientName, Request request, CancellationToken cancellationToken)
	{
		HttpClient client = httpClientFactory.CreateClient(clientName);
		client.MaxResponseContentBufferSize = MaxResponseBytes;
		using var content = new ByteArrayContent(request.Body);
		content.Headers.ContentType = new MediaTypeHeaderValue(request.ContentType);

		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(RequestTimeout);
		using HttpResponseMessage response = await client.PostAsync(request.Url, content, timeout.Token).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		return await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
	}

	/// <summary>
	/// Fetches the directory's OHTTP key configuration (RFC 9540). The directory must not learn
	/// our address at this step either: over Tor the factory's client already hides it, without
	/// Tor the request is tunnelled through a relay acting as a CONNECT proxy.
	/// </summary>
	public static async Task<OhttpKeys> FetchOhttpKeysAsync(PayjoinConfiguration configuration, IHttpClientFactory httpClientFactory, CancellationToken cancellationToken)
	{
		if (configuration.UseTor)
		{
			HttpClient client = httpClientFactory.CreateClient($"payjoin-oneshot-{Guid.NewGuid():N}");
			return await FetchOhttpKeysAsync(client, configuration.DirectoryUri, cancellationToken).ConfigureAwait(false);
		}

		// Without Tor a relay stands in; the next one is tried when a relay cannot be reached.
		int first = Random.Shared.Next(configuration.OhttpRelays.Length);
		for (int attempt = 0; ; attempt++)
		{
			using var relayHandler = new HttpClientHandler { Proxy = new WebProxy(configuration.GetRelay(first + attempt)) };
			using var client = new HttpClient(relayHandler, disposeHandler: false);
			try
			{
				return await FetchOhttpKeysAsync(client, configuration.DirectoryUri, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex) when (attempt + 1 < configuration.OhttpRelays.Length && (ex is HttpRequestException or UniffiException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)))
			{
				// Unreachable, silent, or not answering with a key configuration: on to the next relay.
				Logger.LogWarning($"Payjoin relay {configuration.GetRelay(first + attempt)} did not deliver the directory's keys, trying the next one. {PayjoinErrors.Describe(ex)}");
			}
		}
	}

	private static async Task<OhttpKeys> FetchOhttpKeysAsync(HttpClient client, Uri directoryUri, CancellationToken cancellationToken)
	{
		client.MaxResponseContentBufferSize = MaxResponseBytes;
		using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(directoryUri, "/.well-known/ohttp-gateway"));
		request.Headers.Accept.ParseAdd("application/ohttp-keys");

		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(RequestTimeout);
		using HttpResponseMessage response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		return OhttpKeys.Decode(await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false));
	}
}
