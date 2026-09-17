using System.IO;
using System.Text;
using NBitcoin;

namespace WalletWasabi.Hwi.Passport;

/// <summary>
/// A coinjoin session policy sent to the Passport for one-time on-device approval. The device enforces it for
/// every round: only the named account and coordinator, self-spend outputs, and fees that over the whole
/// session stay within <see cref="FeeBudgetSats"/>. Byte layout matches the firmware's <c>coinjoin::Policy</c>.
/// </summary>
public sealed record CoinjoinPolicy
{
	public required Network Network { get; init; }

	/// <summary>Account index (unhardened) of the accounts the session may sign from.</summary>
	public required uint Account { get; init; }

	/// <summary>Coordinator identifier, as committed into ownership proofs (ASCII).</summary>
	public required string CoordinatorIdentifier { get; init; }

	/// <summary>The most this session as a whole may lose to mining and coordination fees; the device subtracts every signed round from it. Not per round: that would silently multiply by <see cref="MaxRounds"/>.</summary>
	public required ulong FeeBudgetSats { get; init; }

	/// <summary>Maximum number of rounds this session may sign.</summary>
	public required ushort MaxRounds { get; init; }

	/// <summary>Session lifetime in seconds from approval.</summary>
	public required uint ValidForSeconds { get; init; }

	/// <summary><c>[network u8][account u32][len u8][coordinator][fee_budget u64][max_rounds u16][valid_for u32]</c>, all little-endian.</summary>
	public byte[] Serialize()
	{
		var coordinator = Encoding.ASCII.GetBytes(CoordinatorIdentifier);
		if (coordinator.Length > byte.MaxValue)
		{
			throw new ArgumentException("Coordinator identifier is too long for the policy wire format.");
		}

		using var writer = new BinaryWriter(new MemoryStream());
		writer.Write(PassportDevice.NetworkByte(Network));
		writer.Write(Account);
		writer.Write((byte)coordinator.Length);
		writer.Write(coordinator);
		writer.Write(FeeBudgetSats);
		writer.Write(MaxRounds);
		writer.Write(ValidForSeconds);
		return ((MemoryStream)writer.BaseStream).ToArray();
	}
}
