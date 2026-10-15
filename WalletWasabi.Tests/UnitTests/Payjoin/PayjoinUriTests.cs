using NBitcoin;
using WalletWasabi.Userfacing;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Payjoin;

public class PayjoinUriTests
{
	[Fact]
	public void ReceiverUriIsAcceptedByWasabiAddressParser()
	{
		using var key = new Key();
		string address = key.PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Network.Main).ToString();
		string uri = PayjoinTestVectors.CreateReceiverUri(address, 123_456, new PayjoinTestVectors.InMemoryReceiverPersister());

		Assert.Contains("pj=", uri, StringComparison.OrdinalIgnoreCase);
		var parsed = AddressParser.Parse(uri, Network.Main);
		Assert.True(parsed.IsOk, parsed.IsOk ? "" : parsed.Error);
		var bip21 = Assert.IsType<Address.Bip21Uri>(parsed.Value);
		Assert.Equal(address, bip21.Address.ToWif(Network.Main));
		Assert.Equal(0.00123456m, bip21.Amount);
	}

	// Payjoin Dev Kit test vectors: the endpoint must not smuggle credentials and may be percent-encoded.
	[Fact]
	public void EndpointWithUserInfoIsRejected()
	{
		Assert.ThrowsAny<Exception>(() => global::Payjoin.Uri.Parse("bitcoin:12c6DSiU4Rq3P4ZxziKxzrL5LmMBrzjrJX?amount=1&pj=https://example@evil.com?ciao"));
	}

	[Fact]
	public void PercentEncodedEndpointIsAccepted()
	{
		using var uri = global::Payjoin.Uri.Parse("bitcoin:12c6DSiU4Rq3P4ZxziKxzrL5LmMBrzjrJX?amount=1&pj=https%3A%2F%2Fexample.com%3Fciao");
		Assert.Equal("12c6DSiU4Rq3P4ZxziKxzrL5LmMBrzjrJX", uri.Address());
	}
}
