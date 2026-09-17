using System.Collections.Generic;
using System.Linq;
using NBitcoin;
using WalletWasabi.Hwi.Passport;

namespace WalletWasabi.Tests.UnitTests.Hwi;

/// <summary>A Passport that answers from the "all all all" seed and records what it was asked, for the key chain and backend tests.</summary>
internal sealed class FakePassportDevice : IPassportDevice
{
	public static readonly byte[] SessionToken = Enumerable.Repeat((byte)0x42, PassportDevice.TokenLength).ToArray();

	public ExtKey Seed { get; init; } = TestKeyManagers.MasterKey;
	public uint Capabilities { get; init; } = PassportDevice.CapOwnershipProofs | PassportDevice.CapCoinjoinSigning | PassportDevice.CapTaproot;
	public string FirmwareVersion => "fake";
	public bool SupportsTaproot => (Capabilities & PassportDevice.CapTaproot) != 0;
	public bool Alive { get; set; } = true;

	/// <summary>How the fake signs, given the PSBT the wallet built; unset, the PSBT comes back untouched.</summary>
	public Func<PSBT, PSBT>? Signer { get; init; }

	/// <summary>How the fake proves, given the path and commitment the wallet sent.</summary>
	public Func<KeyPath, byte[], byte[]>? Prover { get; init; }

	public List<string> Calls { get; } = new();
	public CoinjoinPolicy? ApprovedPolicy { get; private set; }
	public byte[]? RevokedToken { get; private set; }
	public bool Disposed { get; private set; }

	public bool IsAlive() => Alive;

	public (HDFingerprint Fingerprint, ExtPubKey ExtPubKey) GetXpub(KeyPath keyPath, Network network)
	{
		Calls.Add($"xpub {keyPath}");
		return (Seed.Neuter().PubKey.GetHDFingerPrint(), Seed.Derive(keyPath).Neuter());
	}

	public byte[] AuthorizeCoinJoin(CoinjoinPolicy policy, int approvalTimeoutMs = 120000)
	{
		Calls.Add("authorize");
		ApprovedPolicy = policy;
		return SessionToken.ToArray();
	}

	public byte[] GetOwnershipProof(byte[] sessionToken, KeyPath keyPath, byte[] commitmentData)
	{
		Calls.Add($"proof {Convert.ToHexString(sessionToken)} {keyPath}");
		return Prover!(keyPath, commitmentData);
	}

	public byte[] SignCoinJoin(byte[] sessionToken, byte[] psbt)
	{
		Calls.Add($"sign {Convert.ToHexString(sessionToken)}");
		var parsed = PSBT.Load(psbt, Network.Main);
		return (Signer?.Invoke(parsed) ?? parsed).ToBytes();
	}

	public void RevokeSession(byte[] sessionToken)
	{
		Calls.Add("revoke");
		RevokedToken = sessionToken.ToArray();
	}

	public void Dispose() => Disposed = true;
}
