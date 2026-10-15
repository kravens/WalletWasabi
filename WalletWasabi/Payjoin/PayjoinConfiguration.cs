using System.Linq;
using NBitcoin;

namespace WalletWasabi.Payjoin;

/// <summary>
/// BIP 77 settings: the directory that stores the encrypted messages, the OHTTP relays that hide
/// us from the directory and the most we pay in fees for our own payjoin contribution.
/// </summary>
public record PayjoinConfiguration(Uri DirectoryUri, Uri[] OhttpRelays, FeeRate MaxFeeRate, bool UseTor)
{
	public const string DefaultDirectoryUri = "https://payjo.in";

	/// <summary>The relay set the payjoin-cli reference client ships with.</summary>
	public static readonly string[] DefaultOhttpRelays =
	[
		"https://pj.benalleng.com",
		"https://pj.bobspacebkk.com",
		"https://payjoin.achow101.com",
	];

	public static PayjoinConfiguration Create(string directoryUri, string[] ohttpRelays, decimal maxFeeRateSatPerVb, bool useTor)
	{
		if (ohttpRelays.Length == 0)
		{
			throw new ArgumentException("At least one payjoin OHTTP relay is required.", nameof(ohttpRelays));
		}

		return new(ParseEndpoint(directoryUri, nameof(directoryUri)), ohttpRelays.Select(x => ParseEndpoint(x, nameof(ohttpRelays))).ToArray(), new FeeRate(maxFeeRateSatPerVb), useTor);
	}

	/// <summary>Relays are used one at a time: the same one for a whole session, the next one only after a failure.</summary>
	public Uri GetRelay(int index) => OhttpRelays[(int)((uint)index % OhttpRelays.Length)];

	/// <summary>Payjoin endpoints must be reached over TLS or as onion services (BIP 78).</summary>
	private static Uri ParseEndpoint(string value, string parameterName)
	{
		if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
			|| !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.Host.EndsWith(".onion", StringComparison.OrdinalIgnoreCase))))
		{
			throw new ArgumentException($"Payjoin endpoint '{value}' is not an https or onion URL.", parameterName);
		}

		return uri;
	}
}
