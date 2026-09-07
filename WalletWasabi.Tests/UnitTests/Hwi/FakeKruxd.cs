using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WalletWasabi.Tests.UnitTests.Hwi;

/// <summary>A kruxd that answers from a table: records every request, and returns whatever the test put under the path.</summary>
internal sealed class FakeKruxd : HttpMessageHandler
{
	public List<(string Path, JsonDocument Body)> Requests { get; } = new();

	/// <summary>Reply per endpoint path ("/info", ...): status and body, or a function of the request body.</summary>
	public Dictionary<string, Func<JsonDocument, (HttpStatusCode Status, string Body)>> Replies { get; } = new();

	/// <summary>When set, every request fails as if nothing listened on the port.</summary>
	public bool Unreachable { get; set; }

	/// <summary>When set, every request hangs until the caller gives up.</summary>
	public bool Stalled { get; set; }

	public FakeKruxd Answer(string path, string body, HttpStatusCode status = HttpStatusCode.OK)
	{
		Replies[path] = _ => (status, body);
		return this;
	}

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		if (Unreachable)
		{
			throw new HttpRequestException("connection refused");
		}

		if (Stalled)
		{
			await Task.Delay(Timeout.Infinite, cancellationToken);
		}

		var body = JsonDocument.Parse(request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken));
		Requests.Add((request.RequestUri!.AbsolutePath, body));
		var (status, reply) = Replies[request.RequestUri.AbsolutePath](body);
		return new HttpResponseMessage(status) { Content = new StringContent(reply) };
	}
}
