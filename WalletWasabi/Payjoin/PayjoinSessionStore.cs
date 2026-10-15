using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using NBitcoin;
using WalletWasabi.Io;
using WalletWasabi.Logging;

namespace WalletWasabi.Payjoin;

/// <summary>
/// Receiver sessions on disk. Per session <c>&lt;id&gt;.events</c> is the payjoin event log the
/// library replays (one JSON event per line) and <c>&lt;id&gt;.meta</c> (renamed to
/// <c>&lt;id&gt;.closed</c> when the session ends) holds what Wasabi needs on top of it as key=value
/// lines; a <c>&lt;id&gt;.cancel</c> marker asks the background tick to give the session up.
/// <c>inputs-seen.txt</c> lists every outpoint a sender offered together with the session that saw
/// it, so a sender cannot probe which coins are ours by retrying (BIP 78).
/// </summary>
public class PayjoinSessionStore
{
	private readonly string _directory;
	private readonly Dictionary<OutPoint, string> _inputsSeen = new();
	private readonly Lock _lock = new();

	public PayjoinSessionStore(string directory, Network network)
	{
		_directory = directory;
		Network = network;
		try
		{
			Directory.CreateDirectory(directory);
			if (SafelyExists(InputsSeenPath))
			{
				foreach (string line in File.SafelyReadAllText(InputsSeenPath, Encoding.UTF8).Split('\n'))
				{
					string[] parts = line.Split(' ', 2);
					if (parts.Length == 2 && OutPoint.TryParse(parts[0], out OutPoint? outpoint) && outpoint is { })
					{
						_inputsSeen[outpoint] = parts[1];
					}
				}
			}

			// Half-written session files the safe reader cannot recover go, together with the event log of a session that never came to be.
			foreach (string stray in new[] { "meta", "closed" }.SelectMany(StrayPaths))
			{
				DeleteSafeFile(stray);
			}

			var knownIds = SessionPaths("meta").Concat(SessionPaths("closed")).Select(Path.GetFileNameWithoutExtension).ToHashSet();
			foreach (string orphan in Directory.EnumerateFiles(directory, "*.events").Where(x => !knownIds.Contains(Path.GetFileNameWithoutExtension(x))))
			{
				File.Delete(orphan);
			}

			// Closed sessions are kept for a while so the user can still ask about them.
			foreach (string stale in SessionPaths("closed").Where(x => SafeFileVariants(x).Max(File.GetLastWriteTimeUtc) < DateTime.UtcNow - ClosedSessionRetention))
			{
				DeleteSafeFile(stale);
			}

			// Inputs claimed by sessions that are gone for good go with them, which bounds the file.
			var remainingIds = SessionPaths("meta").Concat(SessionPaths("closed")).Select(Path.GetFileNameWithoutExtension).ToHashSet();
			if (_inputsSeen.Values.Any(x => !remainingIds.Contains(x)))
			{
				foreach (OutPoint forgotten in _inputsSeen.Where(x => !remainingIds.Contains(x.Value)).Select(x => x.Key).ToArray())
				{
					_inputsSeen.Remove(forgotten);
				}

				WriteInputsSeen();
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			Logger.LogWarning($"Could not read the payjoin store in '{directory}': {ex.Message}");
		}
	}

	private static readonly TimeSpan ClosedSessionRetention = TimeSpan.FromDays(90);

	internal Network Network { get; }

	private string InputsSeenPath => Path.Combine(_directory, "inputs-seen.txt");

	/// <summary>Creates the session in memory only; <see cref="PayjoinSession.Persist"/> makes it visible to the store.</summary>
	public PayjoinSession Create(string walletName, string address) =>
		new(this, Guid.NewGuid().ToString("N"), walletName, address, DateTimeOffset.UtcNow);

	public IEnumerable<PayjoinSession> GetOpen() => Load("meta");

	public IEnumerable<PayjoinSession> GetAll() => Load("closed", "meta").OrderBy(x => x.Created).ToArray();

	/// <summary>Claims the outpoint for the session. Returns false when another session already saw it; the same session may look again after a retry.</summary>
	public bool TryMarkInputSeen(OutPoint outpoint, string sessionId)
	{
		lock (_lock)
		{
			if (_inputsSeen.TryGetValue(outpoint, out string? owner))
			{
				return owner == sessionId;
			}

			_inputsSeen[outpoint] = sessionId;
			WriteInputsSeen();
			return true;
		}
	}

	/// <summary>Forgets the inputs a session claimed: it ended without revealing a coin, so the sender may try again.</summary>
	public void ReleaseInputsSeen(string sessionId)
	{
		lock (_lock)
		{
			if (_inputsSeen.Values.All(x => x != sessionId))
			{
				return;
			}

			foreach (OutPoint outpoint in _inputsSeen.Where(x => x.Value == sessionId).Select(x => x.Key).ToArray())
			{
				_inputsSeen.Remove(outpoint);
			}

			WriteInputsSeen();
		}
	}

	/// <summary>Always the whole file through the safe writer, so an interrupted write never shadows the complete copy.</summary>
	private void WriteInputsSeen() =>
		File.SafelyWriteAllText(InputsSeenPath, string.Concat(_inputsSeen.Select(x => $"{x.Key} {x.Value}\n")), Encoding.UTF8);

	/// <summary>A marker file, so the request never races with the tick's own writes to the session file.</summary>
	public void RequestCancel(string id)
	{
		lock (_lock)
		{
			if (!SafelyExists(GetPath(id, "meta")))
			{
				throw new InvalidOperationException($"Payjoin session '{id}' is not open.");
			}

			File.WriteAllText(GetPath(id, "cancel"), "");
		}
	}

	internal bool IsCancelRequested(string id) => File.Exists(GetPath(id, "cancel"));

	internal void AppendEvent(string id, string @event)
	{
		lock (_lock)
		{
			File.AppendAllText(GetPath(id, "events"), @event + "\n");
		}
	}

	internal string[] ReadEvents(string id)
	{
		lock (_lock)
		{
			string path = GetPath(id, "events");
			return File.Exists(path) ? File.ReadAllLines(path).Where(x => x.Length > 0).ToArray() : [];
		}
	}

	internal void Write(PayjoinSession session, string content)
	{
		lock (_lock)
		{
			File.SafelyWriteAllText(GetPath(session.Id, session.IsClosed ? "closed" : "meta"), content, Encoding.UTF8);
			if (session.IsClosed)
			{
				// The event log did its job; the closed file keeps what the user may still ask about.
				DeleteSafeFile(GetPath(session.Id, "meta"));
				File.Delete(GetPath(session.Id, "events"));
				File.Delete(GetPath(session.Id, "cancel"));
			}
		}
	}

	private PayjoinSession[] Load(params string[] extensions)
	{
		lock (_lock)
		{
			// A crash between writing the closed record and deleting the open one leaves both; the closed record wins.
			var closedIds = SessionPaths("closed").Select(Path.GetFileNameWithoutExtension).ToHashSet();
			var sessions = new List<PayjoinSession>();
			foreach (string path in extensions.SelectMany(SessionPaths))
			{
				if (path.EndsWith(".meta", StringComparison.Ordinal) && closedIds.Contains(Path.GetFileNameWithoutExtension(path)!))
				{
					DeleteSafeFile(path);
					continue;
				}

				try
				{
					sessions.Add(PayjoinSession.Load(this, path));
				}
				catch (Exception ex) when (ex is IOException or FormatException or KeyNotFoundException or InvalidOperationException)
				{
					// A torn or hand-edited file must not take the other sessions down.
					Logger.LogWarning($"Skipping unreadable payjoin session file '{path}': {ex.Message}");
				}
			}

			return sessions.ToArray();
		}
	}

	/// <summary>Session files with the given extension that the safe reader can read, including the <c>.old</c> and <c>.new</c> pair an interrupted write leaves behind.</summary>
	private IEnumerable<string> SessionPaths(string extension) => CandidatePaths(extension).Where(SafelyExists);

	/// <summary>Leftovers of an interrupted write that the safe reader cannot recover.</summary>
	private IEnumerable<string> StrayPaths(string extension) => CandidatePaths(extension).Where(x => !SafelyExists(x)).ToArray();

	private IEnumerable<string> CandidatePaths(string extension) =>
		Directory.EnumerateFiles(_directory, $"*.{extension}*")
			.Select(x => x.EndsWith(".new", StringComparison.Ordinal) || x.EndsWith(".old", StringComparison.Ordinal) ? x[..^4] : x)
			.Where(x => x.EndsWith($".{extension}", StringComparison.Ordinal))
			.Distinct();

	/// <summary>The combinations <see cref="SafeFile"/> can read: the file itself, or both halves of an interrupted write.</summary>
	private static bool SafelyExists(string path) => File.Exists(path) || (File.Exists(path + ".old") && File.Exists(path + ".new"));

	private static string[] SafeFileVariants(string path) => [path, path + ".new", path + ".old"];

	private static void DeleteSafeFile(string path)
	{
		foreach (string variant in SafeFileVariants(path))
		{
			File.Delete(variant);
		}
	}

	private string GetPath(string id, string extension) => Path.Combine(_directory, $"{id}.{extension}");
}

public class PayjoinSession : global::Payjoin.JsonReceiverSessionPersister
{
	private readonly PayjoinSessionStore _store;

	internal PayjoinSession(PayjoinSessionStore store, string id, string walletName, string address, DateTimeOffset created)
	{
		_store = store;
		Id = id;
		WalletName = walletName;
		Address = address;
		Created = created;
	}

	public string Id { get; }
	public string WalletName { get; }
	public string Address { get; }
	public DateTimeOffset Created { get; }
	public string? PjUri { get; set; }

	/// <summary>The coin we contribute; kept out of other spends until the session ends.</summary>
	public OutPoint? ReservedOutpoint { get; set; }

	/// <summary>The sender's signed original transaction: it pays us whether or not the payjoin completes.</summary>
	public Transaction? FallbackTx { get; set; }

	public uint256? ProposalTxid { get; set; }

	/// <summary>When broadcasting the fallback first failed while our signed input was out in a proposal.</summary>
	public DateTimeOffset? FallbackFailedSince { get; set; }

	/// <summary>The read error behind an empty event log, so a replay failure can be told apart from a broken log.</summary>
	public IOException? LoadError { get; private set; }

	public bool IsClosed { get; set; }

	/// <summary>Set by the user through the RPC; the next tick gives the session up.</summary>
	public bool CancelRequested => _store.IsCancelRequested(Id);

	public string? Error { get; set; }

	public void Save(string @event) => _store.AppendEvent(Id, @event);

	public string[] Load()
	{
		try
		{
			LoadError = null;
			return _store.ReadEvents(Id);
		}
		catch (IOException ex)
		{
			LoadError = ex;
			return [];
		}
	}

	public void Close()
	{
		IsClosed = true;
		Persist();
	}

	public void Persist()
	{
		var lines = new List<string>
		{
			$"Wallet={WalletName}",
			$"Address={Address}",
			$"Created={Created.ToString("O", CultureInfo.InvariantCulture)}",
		};
		if (PjUri is { } uri)
		{
			lines.Add($"PjUri={uri}");
		}
		if (ReservedOutpoint is { } outpoint)
		{
			lines.Add($"Reserved={outpoint}");
		}
		if (FallbackTx is { } fallbackTx)
		{
			lines.Add($"FallbackTx={fallbackTx.ToHex()}");
		}
		if (ProposalTxid is { } txid)
		{
			lines.Add($"ProposalTxid={txid}");
		}
		if (FallbackFailedSince is { } failedSince)
		{
			lines.Add($"FallbackFailedSince={failedSince.ToString("O", CultureInfo.InvariantCulture)}");
		}
		if (Error is { } error)
		{
			lines.Add($"Error={error.ReplaceLineEndings(" ")}");
		}

		_store.Write(this, string.Join('\n', lines) + "\n");
	}

	internal static PayjoinSession Load(PayjoinSessionStore store, string path)
	{
		var values = File.SafelyReadAllText(path, Encoding.UTF8).Split('\n')
			.Select(x => x.Split('=', 2))
			.Where(x => x.Length == 2)
			.ToDictionary(x => x[0], x => x[1]);

		return new PayjoinSession(store, Path.GetFileNameWithoutExtension(path), values["Wallet"], values["Address"], DateTimeOffset.Parse(values["Created"], CultureInfo.InvariantCulture))
		{
			IsClosed = Path.GetExtension(path) == ".closed",
			PjUri = values.GetValueOrDefault("PjUri"),
			ReservedOutpoint = values.TryGetValue("Reserved", out var reserved) ? OutPoint.Parse(reserved) : null,
			FallbackTx = values.TryGetValue("FallbackTx", out var fallbackTx) ? Transaction.Parse(fallbackTx, store.Network) : null,
			ProposalTxid = values.TryGetValue("ProposalTxid", out var txid) ? uint256.Parse(txid) : null,
			FallbackFailedSince = values.TryGetValue("FallbackFailedSince", out var failedSince) ? DateTimeOffset.Parse(failedSince, CultureInfo.InvariantCulture) : null,
			Error = values.GetValueOrDefault("Error"),
		};
	}
}
