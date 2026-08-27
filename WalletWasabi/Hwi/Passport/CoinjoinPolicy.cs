using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using NBitcoin;

namespace WalletWasabi.Hwi.Passport;

/// <summary>
/// A coinjoin session policy sent to the Passport for one-time on-device approval. The device enforces it for
/// every round: only the named account and coordinator, self-spend outputs, and a fee contribution per round
/// no greater than <see cref="MaxFeeContributionSats"/>. Byte layout matches the firmware's
/// <c>coinjoin::Policy</c> (see FIRMWARE_PLAN.md).
/// </summary>
public sealed record CoinjoinPolicy
{
	public required Network Network { get; init; }

	/// <summary>BIP-84 account index (unhardened).</summary>
	public required uint Account { get; init; }

	/// <summary>Coordinator identifier, as committed into ownership proofs (ASCII).</summary>
	public required string CoordinatorIdentifier { get; init; }

	/// <summary>Maximum sats this wallet may lose in one round (mining + coordination fee share).</summary>
	public required ulong MaxFeeContributionSats { get; init; }

	/// <summary>Maximum number of rounds this session may sign.</summary>
	public required ushort MaxRounds { get; init; }

	/// <summary>Session lifetime in seconds from approval.</summary>
	public required uint ValidForSeconds { get; init; }

	public byte[] Serialize()
	{
		var coordinator = Encoding.ASCII.GetBytes(CoordinatorIdentifier);
		if (coordinator.Length > 0xfc)
		{
			throw new ArgumentException("Coordinator identifier is too long for the policy wire format.");
		}

		var bytes = new List<byte>(6 + coordinator.Length + 14)
		{
			PassportDevice.NetworkByte(Network),
		};
		bytes.AddRange(UInt32Le(Account));
		bytes.Add((byte)coordinator.Length);
		bytes.AddRange(coordinator);
		bytes.AddRange(UInt64Le(MaxFeeContributionSats));
		bytes.AddRange(UInt16Le(MaxRounds));
		bytes.AddRange(UInt32Le(ValidForSeconds));
		return bytes.ToArray();
	}

	private static byte[] UInt16Le(ushort v)
	{
		var b = new byte[2];
		BinaryPrimitives.WriteUInt16LittleEndian(b, v);
		return b;
	}

	private static byte[] UInt32Le(uint v)
	{
		var b = new byte[4];
		BinaryPrimitives.WriteUInt32LittleEndian(b, v);
		return b;
	}

	private static byte[] UInt64Le(ulong v)
	{
		var b = new byte[8];
		BinaryPrimitives.WriteUInt64LittleEndian(b, v);
		return b;
	}
}
