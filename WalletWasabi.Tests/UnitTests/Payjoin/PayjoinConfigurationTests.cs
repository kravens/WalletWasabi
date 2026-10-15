using WalletWasabi.Payjoin;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Payjoin;

public class PayjoinConfigurationTests
{
	[Fact]
	public void DefaultsAreValid()
	{
		var configuration = PayjoinConfiguration.Create(PayjoinConfiguration.DefaultDirectoryUri, PayjoinConfiguration.DefaultOhttpRelays, 50m, useTor: true);
		Assert.Equal(3, configuration.OhttpRelays.Length);
		Assert.Equal(configuration.OhttpRelays[1], configuration.GetRelay(4));
		Assert.Equal(configuration.OhttpRelays[0], configuration.GetRelay(3));
	}

	[Theory]
	[InlineData("http://relay.example")]
	[InlineData("relay.example")]
	[InlineData("")]
	public void RejectsInsecureOrMalformedRelays(string relay)
	{
		Assert.Throws<ArgumentException>(() => PayjoinConfiguration.Create(PayjoinConfiguration.DefaultDirectoryUri, [relay], 50m, useTor: true));
	}

	[Fact]
	public void RejectsEmptyRelayList()
	{
		Assert.Throws<ArgumentException>(() => PayjoinConfiguration.Create(PayjoinConfiguration.DefaultDirectoryUri, [], 50m, useTor: true));
	}

	[Fact]
	public void AcceptsOnionEndpoints()
	{
		var configuration = PayjoinConfiguration.Create("http://vjdpwgybvubne5hda6v4c5iaeeevhge6jvo3w2cl6eocbwwvwxp7b7qd.onion", ["http://abcdefghijklmnop.onion"], 50m, useTor: true);
		Assert.Equal("http", configuration.DirectoryUri.Scheme);
	}
}
