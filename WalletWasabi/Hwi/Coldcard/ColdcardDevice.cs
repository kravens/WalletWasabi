using System.Buffers.Binary;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using NBitcoin;
using WalletWasabi.Logging;

namespace WalletWasabi.Hwi.Coldcard;

/// <summary>
/// A connected Coldcard, driven over its raw USB protocol (no bridge daemon). Opens the HID channel,
/// establishes AES link encryption, and exposes the typed commands the coinjoin flow needs. Ownership
/// proofs use the <c>slp9</c> command added by the accompanying firmware change; PSBT signing under an HSM
/// policy is layered on top for the key chain.
/// </summary>
public sealed class ColdcardDevice : IDisposable
{
	private ColdcardTransport _transport;
	private readonly string? _serialNumber;

	/// <summary>Serialises every exchange with the device. There is one USB pipe and the protocol has no
	/// request/response correlation, so two overlapping commands interleave their frames and leave the
	/// AES-CTR counters out of step for the rest of the session. Wasabi registers inputs with
	/// Task.WhenAll, so any round contributing more than one input hits this immediately: observed as a
	/// round that registered two inputs and then died with an unreadable reply. Reentrant, so a command
	/// may call another (StartHsm polls GetHsmStatus).</summary>
	private readonly object _gate = new();

	/// <summary>Identifies the HSM policy this session installed, so a re-authorization can tell it apart
	/// from a policy that was already running when we connected.</summary>
	private string? _installedPolicyHash;

	private ColdcardDevice(ColdcardTransport transport, string? serialNumber)
	{
		_transport = transport;
		_serialNumber = serialNumber;
	}

	/// <summary>
	/// Replaces the session if the encrypted link has lost sync. Nothing is recoverable once the AES-CTR
	/// counters are apart, but the device itself is fine — a fresh handshake works immediately — so
	/// reconnect rather than leaving every later command to fail with nonsense. HSM mode lives on the
	/// device, so an active policy survives this.
	/// </summary>
	private void EnsureHealthySession()
	{
		if (_transport.IsHealthy)
		{
			return;
		}

		Logger.LogWarning("The Coldcard link lost sync; reconnecting to the device.");
		_transport.Dispose();

		var transport = new ColdcardTransport(ColdcardUsb.Open(_serialNumber));
		try
		{
			var (fingerprint, masterXpub) = transport.StartEncryption();
			if (fingerprint != MasterFingerprint)
			{
				throw new ColdcardException(
					"a different Coldcard is now connected. Reconnect the wallet's own device.",
					"Wrong Coldcard connected");
			}

			_transport = transport;
			MasterXpub = masterXpub;
		}
		catch
		{
			transport.Dispose();
			throw;
		}
	}

	public uint MasterFingerprint { get; private set; }
	public string MasterXpub { get; private set; } = "";

	/// <summary>Opens the connected Coldcard (optionally pinned by serial) and establishes encryption.</summary>
	public static ColdcardDevice Open(string? serialNumber = null)
	{
		var transport = new ColdcardTransport(ColdcardUsb.Open(serialNumber));
		try
		{
			var device = new ColdcardDevice(transport, serialNumber);
			var (fingerprint, masterXpub) = transport.StartEncryption();
			device.MasterFingerprint = fingerprint;
			device.MasterXpub = masterXpub;
			return device;
		}
		catch
		{
			transport.Dispose();
			throw;
		}
	}

	/// <summary>Multi-line version string (firmware version, git hash, model, etc.).</summary>
	public string GetVersion()
	{
		lock (_gate)
		{
			EnsureHealthySession();
			var (_, payload) = _transport.SendReceive(Encoding.ASCII.GetBytes("vers"));
			return Encoding.ASCII.GetString(payload);
		}
	}

	/// <summary>Extended public key at the given derivation path.</summary>
	public BitcoinExtPubKey GetXpub(KeyPath keyPath, Network network)
	{
		lock (_gate)
		{
			EnsureHealthySession();
			var request = Encoding.ASCII.GetBytes("xpub" + $"m/{keyPath}");
			var (_, payload) = _transport.SendReceive(request);
			return new BitcoinExtPubKey(Encoding.ASCII.GetString(payload).TrimEnd('\0'), network);
		}
	}

	/// <summary>
	/// Produces a SLIP-19 ownership proof for a coinjoin input (firmware <c>slp9</c> command). The returned
	/// bytes are the fully serialized proof, ready for <c>OwnershipProof.FromBytes</c> and the coordinator.
	/// </summary>
	public byte[] SignOwnershipProof(KeyPath keyPath, ScriptPubKeyType scriptType, byte[] commitmentData, bool userConfirmation = true)
	{
		lock (_gate)
		{
			EnsureHealthySession();
			var subpath = Encoding.ASCII.GetBytes($"m/{keyPath}");
			byte flags = (byte)(userConfirmation ? 0x01 : 0x00);

			// 'slp9' layout '<4sIIII>': tag ‖ addr_fmt ‖ flags ‖ subpath length ‖ commitment length. The
			// address format is stated rather than left for the device to infer from the path, so the proof
			// is over the script the coordinator actually holds for this input.
			var header = new byte[20];
			Encoding.ASCII.GetBytes("slp9").CopyTo(header, 0);
			BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), AddressFormatOf(scriptType));
			BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), flags);
			BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)subpath.Length);
			BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)commitmentData.Length);

			var request = header.Concat(subpath).Concat(commitmentData).ToArray();
			var (tag, payload) = _transport.SendReceive(request);

			// The firmware answers a proof with 'biny'. Anything else parsed as a proof would surface much
			// later as an opaque "Invalid version magic" from the deserializer, so name the real problem here.
			if (tag != "biny")
			{
				_transport.MarkUnhealthy();
				throw new ColdcardException($"expected an ownership proof but the device replied '{tag}'.");
			}

			return payload;
		}
	}

	/// <summary>The policy's own words for the last refusal, if the device will tell us. Best-effort: the
	/// refusal itself is the news, so a failure to read the reason must not replace it.</summary>
	private string DescribeLastRefusal()
	{
		try
		{
			var (_, payload) = _transport.SendReceive(Encoding.ASCII.GetBytes("hsts"));
			using var status = JsonDocument.Parse(payload);
			if (status.RootElement.TryGetProperty("last_refusal", out var reason)
				&& reason.GetString() is { Length: > 0 } text)
			{
				return text.Contains("transaction count", StringComparison.OrdinalIgnoreCase)
					? $"{text}. The authorized number of transactions is used up: reboot the Coldcard and authorize coinjoining again."
					: text;
			}
		}
		catch (Exception e)
		{
			Logger.LogDebug($"Could not read the Coldcard's refusal reason: {e.Message}");
		}

		return "Check the device screen for the reason.";
	}

	/// <summary>The refusal reason trimmed to fit the coinjoin status line, which is about 45 characters
	/// and truncates without an ellipsis. The device's own words are the most useful thing we can show -
	/// "too few inputs: 4, need 21" tells the user exactly what to change - so keep them when they fit and
	/// fall back to something generic only when they do not. The full text always goes to the log.</summary>
	private static string ShortRefusal(string reason)
	{
		// The device prefixes its reasons with which rule tripped; the number means nothing to the user.
		var text = reason;
		var colon = text.IndexOf(": ", StringComparison.Ordinal);
		if (text.StartsWith("rule #", StringComparison.OrdinalIgnoreCase) && colon > 0)
		{
			text = text[(colon + 2)..];
		}

		// One sentence only: some reasons carry a follow-up telling the user what to do, which the log keeps.
		var stop = text.IndexOf(". ", StringComparison.Ordinal);
		if (stop > 0)
		{
			text = text[..stop];
		}

		text = text.Trim().TrimEnd('.');
		return text.Length is > 0 and <= 34 ? $"Refused: {text}" : "Device refused to sign";
	}

	/// <summary>The firmware's AF_* address format constant (see its <c>public_constants.py</c>).</summary>
	private static uint AddressFormatOf(ScriptPubKeyType scriptType) => scriptType switch
	{
		ScriptPubKeyType.Segwit => 0x07,          // AF_P2WPKH = AFC_PUBKEY(1) | AFC_SEGWIT(2) | AFC_BECH32(4)
		ScriptPubKeyType.TaprootBIP86 => 0x23,    // AF_P2TR   = AFC_PUBKEY(1) | AFC_SEGWIT(2) | AFC_BECH32M(0x20)
		_ => throw new NotSupportedException($"A Coldcard ownership proof cannot be made for '{scriptType}'."),
	};

	/// <summary>
	/// Installs an HSM policy (JSON) and enters HSM mode. The user reviews and approves the policy on the
	/// device; afterwards coinjoin PSBTs and ownership proofs are signed unattended within the policy.
	/// </summary>
	/// <param name="previouslyApprovedHash">The policy hash this wallet recorded the last time a policy was
	/// approved, if any. HSM mode survives a restart of this program but the in-memory record does not, so
	/// without it every reconnect would look like somebody else's policy.</param>
	/// <returns>The hash of the policy now active, when this wallet approved it — freshly, or previously and
	/// matched here. Null when the device is running something else, so a policy nobody agreed to never gets
	/// recorded as the approved one.</returns>
	public string? StartHsm(string policyJson, string? previouslyApprovedHash, string? previouslyApprovedFingerprint, CancellationToken cancellationToken)
	{
		lock (_gate)
		{
			EnsureHealthySession();
			var current = GetHsmStatus();
			if (current.Active)
			{
				// HSM mode is a one-way trip until reboot, so a policy is already running. If it is the one
				// we installed, this is just a re-authorization and there is nothing to do.
				var approved = _installedPolicyHash ?? previouslyApprovedHash;
				if (approved is not null && approved == current.PolicyHash)
				{
					// Right policy, but is it still the one being asked for? The device hash proves what it
					// enforces and nothing about what the user has since configured, so a limit edited while
					// the device sat in HSM would otherwise be saved, shown as active, and quietly not
					// applied. Tightening a limit and not getting it is the bad direction, so stop rather
					// than coinjoin under rules nobody agreed to.
					var wanted = ColdcardHsmPolicy.Fingerprint(policyJson);
					if (previouslyApprovedFingerprint is not null && previouslyApprovedFingerprint != wanted)
					{
						throw new ColdcardException(
							"The coinjoin limits here have changed since this Coldcard approved its policy, and "
							+ "HSM mode cannot be changed while it is running. The device is still enforcing the "
							+ "previous limits. Reboot the Coldcard and start coinjoin again to approve the new "
							+ "ones.",
							"Reset Coldcard / Exit HSM");
					}

					Logger.LogInfo($"The Coldcard is running the policy this wallet approved (hash {approved}).");
					return current.PolicyHash;
				}

				// Otherwise the device is enforcing a policy nobody here installed - typically one the user
				// approved on the device itself, since installing one needs physical approval. Not an attack
				// then, but the limits it enforces are not the ones being shown, so do not pass silently.
				// Refusing would break the legitimate "set the policy on the device first" workflow, so this
				// warns and continues; the device policy still bounds what can be signed either way.
				Logger.LogWarning(
					"The Coldcard is already in HSM mode under a policy this wallet did not approve "
					+ $"(device reports hash {current.PolicyHash ?? "unknown"}"
					+ (previouslyApprovedHash is null ? "" : $", expected {previouslyApprovedHash}")
					+ "). The limits it enforces are not the ones configured here. Reboot the Coldcard and "
					+ "authorize again to apply these limits.");

				// Null, not the hash: nobody approved this policy through this wallet, and recording it would
				// turn a stranger's policy into the one we vouch for on the next connection.
				return null;
			}

			var data = Encoding.UTF8.GetBytes(policyJson);
			var sha = UploadFile(data);

			// 'hsms': length + sha of the uploaded policy. The device replies immediately and then shows the
			// policy on its screen; the decision has to be observed by polling the HSM status.
			var request = new byte[40];
			Encoding.ASCII.GetBytes("hsms").CopyTo(request, 0);
			BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), (uint)data.Length);
			sha.CopyTo(request, 8);
			_transport.SendReceive(request);

			// 'approval_wait' while the story is on screen, 'active' once approved, neither means refused.
			// Generous window: the user reads the whole policy, confirms, then keys an anti-fat-finger digit,
			// and on hardware three minutes turned out to be short for a first-time read. Giving up early is
			// not destructive — the device keeps the prompt up, and a later approval is picked up by the
			// already-active check above — but it does surface a spurious error, so don't rush it.
			var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(10);
			while (true)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var status = GetHsmStatus();
				if (status.Active)
				{
					// Remember which policy this is, so a later re-authorization can tell our own policy
					// apart from one that was already running when we arrived.
					_installedPolicyHash = status.PolicyHash;
					Logger.LogInfo($"The Coldcard accepted the coinjoin policy (hash {status.PolicyHash}).");
					if (status.Summary is { Length: > 0 } summary)
					{
						Logger.LogInfo($"Policy the device will enforce:{Environment.NewLine}{summary}");
					}

					return status.PolicyHash;
				}
				if (!status.ApprovalWait)
				{
					throw new ColdcardException(
						"The HSM policy was refused on the device.",
						"Policy refused on device");
				}
				if (DateTime.UtcNow > deadline)
				{
					throw new ColdcardException(
						"Timed out waiting for the HSM policy approval on the device.",
						"Policy approval timed out");
				}
				Thread.Sleep(500);
			}
		}
	}

	/// <summary>The device's HSM state ('hsts' command): whether a policy is active, whether one is
	/// currently on screen waiting for the user's approval, the hash identifying the running policy, and
	/// the device's own plain-language rendering of it — the same words it printed on its screen, which is
	/// what lets someone confirm the limits being enforced are the limits they chose.</summary>
	public (bool Active, bool ApprovalWait, string? PolicyHash, string? Summary) GetHsmStatus()
	{
		lock (_gate)
		{
			EnsureHealthySession();
			byte[] payload;
			try
			{
				(_, payload) = _transport.SendReceive(Encoding.ASCII.GetBytes("hsts"));
			}
			catch (ColdcardException e) when (e.Message.Contains("HSM commands disabled"))
			{
				// Both 'hsts' and 'hsms' sit in the firmware's HSM_DISABLE_CMDS set, and a factory-fresh
				// device ships with the setting off, so say where to turn it on instead of echoing the device.
				throw new ColdcardException(
					"HSM commands are disabled on this Coldcard. Enable them on the device at "
					+ "Settings > Advanced/Tools > Spending Policy > HSM Mode > Enable, then try again.");
			}

			using var status = JsonDocument.Parse(payload);
			return (
				status.RootElement.TryGetProperty("active", out var active) && active.GetBoolean(),
				status.RootElement.TryGetProperty("approval_wait", out var wait) && wait.GetBoolean(),
				status.RootElement.TryGetProperty("policy_hash", out var hash) ? hash.GetString() : null,
				// Absent when the policy sets priv_over_ux, which suppresses it deliberately.
				status.RootElement.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.String
					? summary.GetString()
					: null);
		}
	}

	/// <summary>
	/// Signs a PSBT on the device (partial: only inputs this wallet owns) and returns the signed PSBT. Under
	/// an HSM policy this happens unattended; otherwise the device prompts for approval.
	/// </summary>
	public byte[] SignPsbt(byte[] psbt, CancellationToken cancellationToken)
	{
		lock (_gate)
		{
			EnsureHealthySession();
			var sha = UploadFile(psbt);

			// 'stxn' layout '<4sII32s>': tag ‖ length ‖ flags (0 = do not finalize, return signed PSBT) ‖ sha.
			var request = new byte[44];
			Encoding.ASCII.GetBytes("stxn").CopyTo(request, 0);
			BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), (uint)psbt.Length);
			BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8), 0);
			sha.CopyTo(request, 12);
			_transport.SendReceive(request);

			var (length, resultSha) = PollForSignedFile(cancellationToken);
			return DownloadFile(length, resultSha, fileNumber: 1);
		}
	}

	/// <summary>Uploads a file in blocks and verifies the device's checksum; returns its SHA-256.</summary>
	private byte[] UploadFile(byte[] data)
	{
		const int BlockSize = 1024;
		for (int offset = 0; offset < data.Length; offset += BlockSize)
		{
			int here = Math.Min(BlockSize, data.Length - offset);
			// 'upld' layout: tag(4) ‖ offset(u32) ‖ total_size(u32) ‖ data.
			var request = new byte[12 + here];
			Encoding.ASCII.GetBytes("upld").CopyTo(request, 0);
			BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), (uint)offset);
			BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8), (uint)data.Length);
			Array.Copy(data, offset, request, 12, here);
			_transport.SendReceive(request);
		}

		var expected = System.Security.Cryptography.SHA256.HashData(data);
		var (_, deviceSha) = _transport.SendReceive(Encoding.ASCII.GetBytes("sha2"));
		if (!deviceSha.AsSpan().SequenceEqual(expected))
		{
			_transport.MarkUnhealthy();
			throw new ColdcardException("checksum mismatch during file upload; the link may have lost sync.");
		}
		return expected;
	}

	/// <summary>The firmware's <c>MAX_TXN_LEN_MK4</c>: nothing we ask the device for can exceed it, so it is
	/// the ceiling on a length the device reports back to us.</summary>
	private const uint MaxTransferLength = 2 * 1024 * 1024;

	/// <summary>Downloads a signed file (file 1) block by block and checks its SHA-256.</summary>
	private byte[] DownloadFile(uint length, byte[] expectedSha, int fileNumber)
	{
		// The link is AES-CTR with no MAC, so a corrupted length field arrives looking legitimate. Bound
		// it before it becomes an allocation.
		if (length > MaxTransferLength)
		{
			throw new ColdcardException($"The Coldcard reported an implausible {length}-byte result; refusing it.");
		}

		const int BlockSize = 1024;
		var result = new byte[length];
		for (uint offset = 0; offset < length; offset += BlockSize)
		{
			uint here = Math.Min(BlockSize, length - offset);
			var request = new byte[16];
			Encoding.ASCII.GetBytes("dwld").CopyTo(request, 0);
			BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), offset);
			BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8), here);
			BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(12), (uint)fileNumber);
			var (_, chunk) = _transport.SendReceive(request);
			chunk.CopyTo(result, (int)offset);
		}

		if (!System.Security.Cryptography.SHA256.HashData(result).AsSpan().SequenceEqual(expectedSha))
		{
			_transport.MarkUnhealthy();
			throw new ColdcardException("checksum mismatch during file download; the link may have lost sync.");
		}
		return result;
	}

	private (uint Length, byte[] Sha) PollForSignedFile(CancellationToken cancellationToken)
	{
		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var (tag, payload) = _transport.SendReceive(Encoding.ASCII.GetBytes("stok"));
			if (tag == "strx") // done: <I32s> length + sha
			{
				if (payload.Length < 36)
				{
					throw new ColdcardException($"The Coldcard sent a {payload.Length}-byte signing result header; expected 36.");
				}
				return (BinaryPrimitives.ReadUInt32LittleEndian(payload), payload[4..36]);
			}
			if (tag == "refu")
			{
				// The policy records why it said no, and the reasons are all things the user has to act on:
				// the self-transfer floor, an unlisted path, or the transaction count being used up (which
				// needs a reboot, since an HSM session cannot be reset). Bare "refused" would send them
				// looking in the wrong place.
				var refusal = DescribeLastRefusal();
				throw new ColdcardException($"signing was refused by the device. {refusal}", ShortRefusal(refusal));
			}
			// 'okay' (empty) means still working — poll again.
			Thread.Sleep(250);
		}
	}

	public void Dispose() => _transport.Dispose();
}
