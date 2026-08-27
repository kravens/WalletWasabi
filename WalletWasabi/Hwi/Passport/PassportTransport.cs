using System.Buffers.Binary;

namespace WalletWasabi.Hwi.Passport;

/// <summary>
/// One request/response conversation with a Passport Prime wallet-rpc service. Frames a request onto the HID
/// channel and reassembles the reply. The wire protocol (v1) is plaintext (the channel is a local USB cable,
/// and every state-changing action is gated by an on-device policy approval, so there is no link encryption
/// like Coldcard's): request <c>[ver][cmd][len u16][payload]</c>, response adds a <c>status</c> byte after
/// the command echo.
/// </summary>
public sealed class PassportTransport : IDisposable
{
	public const byte ProtocolVersion = 1;

	private readonly IPassportHid _hid;

	public PassportTransport(IPassportHid hid)
	{
		_hid = hid;
	}

	/// <summary>Sends a command and returns the response payload, throwing <see cref="PassportException"/> on a non-OK status.</summary>
	public byte[] SendReceive(byte command, byte[] payload, int timeoutMs = 15000)
	{
		var request = new byte[4 + payload.Length];
		request[0] = ProtocolVersion;
		request[1] = command;
		BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(2), (ushort)payload.Length);
		payload.CopyTo(request, 4);

		foreach (var report in PassportFraming.PackRequest(request))
		{
			_hid.WriteReport(report);
		}

		byte[] response = PassportFraming.ReadResponse(() => _hid.ReadReport(timeoutMs));
		if (response.Length < 5)
		{
			throw new PassportException("Truncated Passport response.");
		}

		byte status = response[2];
		int len = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(3));
		if (response.Length < 5 + len)
		{
			throw new PassportException("Passport response shorter than its declared length.");
		}

		var responsePayload = response[5..(5 + len)];
		if (status != PassportStatus.Ok)
		{
			throw new PassportException($"Passport command 0x{command:x2} failed: {PassportStatus.Describe(status)}.");
		}
		return responsePayload;
	}

	public void Dispose() => _hid.Dispose();
}

/// <summary>wallet-rpc protocol v1 command ids (see firmware <c>protocol.rs</c>).</summary>
public static class PassportCommand
{
	public const byte GetInfo = 0x01;
	public const byte GetXpub = 0x02;
	public const byte GetOwnershipProof = 0x03;
	public const byte AuthorizeCoinjoin = 0x04;
	public const byte SignCoinjoin = 0x05;
	public const byte RevokeSession = 0x06;
}

/// <summary>wallet-rpc protocol v1 status codes.</summary>
public static class PassportStatus
{
	public const byte Ok = 0x00;
	public const byte Malformed = 0x01;
	public const byte UnknownCommand = 0x02;
	public const byte UnsupportedVersion = 0x03;
	public const byte Denied = 0x04;
	public const byte NoSession = 0x05;
	public const byte Policy = 0x06;
	public const byte Internal = 0x07;

	public static string Describe(byte status) => status switch
	{
		Malformed => "malformed request",
		UnknownCommand => "unknown command",
		UnsupportedVersion => "unsupported protocol version",
		Denied => "denied by user or seed unavailable",
		NoSession => "no valid coinjoin session",
		Policy => "request outside authorized policy",
		Internal => "internal device error",
		_ => $"status 0x{status:x2}",
	};
}

public class PassportException : Exception
{
	public PassportException(string message) : base("Passport: " + message)
	{
	}
}
