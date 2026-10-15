using Payjoin;

namespace WalletWasabi.Tests.UnitTests.Payjoin;

internal static class PayjoinTestVectors
{
	/// <summary>OHTTP key configuration from the Payjoin Dev Kit test suite.</summary>
	private static readonly byte[] OhttpKeysBytes =
	[
		0x01, 0x00, 0x16, 0x04, 0xba, 0x48, 0xc4, 0x9c, 0x3d, 0x4a,
		0x92, 0xa3, 0xad, 0x00, 0xec, 0xc6, 0x3a, 0x02, 0x4d, 0xa1,
		0x0c, 0xed, 0x02, 0x18, 0x0c, 0x73, 0xec, 0x12, 0xd8, 0xa7,
		0xad, 0x2c, 0xc9, 0x1b, 0xb4, 0x83, 0x82, 0x4f, 0xe2, 0xbe,
		0xe8, 0xd2, 0x8b, 0xfe, 0x2e, 0xb2, 0xfc, 0x64, 0x53, 0xbc,
		0x4d, 0x31, 0xcd, 0x85, 0x1e, 0x8a, 0x65, 0x40, 0xe8, 0x6c,
		0x53, 0x82, 0xaf, 0x58, 0x8d, 0x37, 0x09, 0x57, 0x00, 0x04,
		0x00, 0x01, 0x00, 0x03,
	];

	public const string Directory = "https://example.com";

	public static OhttpKeys OhttpKeys => OhttpKeys.Decode(OhttpKeysBytes);

	/// <summary>A BIP 77 URI as a receiver would hand it out, paying <paramref name="address"/>.</summary>
	public static string CreateReceiverUri(string address, ulong? amountSats, global::Payjoin.JsonReceiverSessionPersister persister)
	{
		using var builder = new ReceiverBuilder(address, Directory, OhttpKeys);
		using InitialReceiveTransition transition = builder.Build();
		using Initialized initialized = transition.Save(persister);
		using PjUri pjUri = initialized.PjUri();
		if (amountSats is { } sats)
		{
			using PjUri withAmount = pjUri.SetAmountSats(sats);
			return withAmount.AsString();
		}

		return pjUri.AsString();
	}

	internal sealed class InMemoryReceiverPersister : global::Payjoin.JsonReceiverSessionPersister
	{
		public System.Collections.Generic.List<string> Events { get; } = [];
		public bool Closed { get; private set; }

		public void Save(string @event) => Events.Add(@event);

		public string[] Load() => Events.ToArray();

		public void Close() => Closed = true;
	}
}
