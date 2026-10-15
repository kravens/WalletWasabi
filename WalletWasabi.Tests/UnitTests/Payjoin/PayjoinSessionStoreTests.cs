using System.IO;
using System.Linq;
using NBitcoin;
using Payjoin;
using WalletWasabi.Payjoin;
using WalletWasabi.Tests.Helpers;
using Xunit;
using OutPoint = NBitcoin.OutPoint;

namespace WalletWasabi.Tests.UnitTests.Payjoin;

public class PayjoinSessionStoreTests
{
	private const string Address = "tb1q6d3a2w975yny0asuvd9a67ner4nks58ff0q8g4";

	private static string NewDirectory() => Path.Combine(Common.GetWorkDir(), Guid.NewGuid().ToString("N"));

	[Fact]
	public void SessionSurvivesReload()
	{
		string directory = NewDirectory();
		var store = new PayjoinSessionStore(directory, Network.Main);
		PayjoinSession session = store.Create("Wallet", Address);
		Assert.Empty(store.GetAll());

		session.PjUri = PayjoinTestVectors.CreateReceiverUri(session.Address, 1234, session);
		session.ReservedOutpoint = BitcoinFactory.CreateOutPoint();
		session.FallbackTx = BitcoinFactory.CreateTransaction();
		session.ProposalTxid = BitcoinFactory.CreateUint256();
		session.FallbackFailedSince = DateTimeOffset.UtcNow.AddMinutes(-5);
		session.Error = "line one\nline two";
		session.Persist();

		PayjoinSession reloaded = Assert.Single(new PayjoinSessionStore(directory, Network.Main).GetAll());
		Assert.Equal(session.Id, reloaded.Id);
		Assert.Equal(session.WalletName, reloaded.WalletName);
		Assert.Equal(session.Address, reloaded.Address);
		Assert.Equal(session.Created, reloaded.Created);
		Assert.Equal(session.PjUri, reloaded.PjUri);
		Assert.Equal(session.ReservedOutpoint, reloaded.ReservedOutpoint);
		Assert.Equal(session.FallbackTx.GetHash(), reloaded.FallbackTx?.GetHash());
		Assert.Equal(session.ProposalTxid, reloaded.ProposalTxid);
		Assert.Equal(session.FallbackFailedSince, reloaded.FallbackFailedSince);
		Assert.Equal("line one line two", reloaded.Error);
		Assert.False(reloaded.IsClosed);

		// The library replays the persisted event log into the state it was left in.
		using ReplayResult replay = PayjoinMethods.ReplayReceiverEventLog(reloaded);
		using ReceiveSession state = replay.State();
		Assert.IsType<ReceiveSession.Initialized>(state);
		using ReceiverSessionHistory history = replay.SessionHistory();
		using PjUri pjUri = history.PjUri();
		Assert.Equal(session.Address, pjUri.Address());
	}

	[Fact]
	public void CancelClosesTheSession()
	{
		var store = new PayjoinSessionStore(NewDirectory(), Network.Main);
		PayjoinSession session = store.Create("Wallet", Address);
		session.PjUri = PayjoinTestVectors.CreateReceiverUri(session.Address, null, session);
		session.Persist();
		Assert.Single(store.GetOpen());

		using (ReplayResult replay = PayjoinMethods.ReplayReceiverEventLog(session))
		using (ReceiveSession state = replay.State())
		{
			var initialized = Assert.IsType<ReceiveSession.Initialized>(state);
			using CancelTransition cancel = initialized.Inner.Cancel();
			Assert.Null(cancel.Save(session));
		}

		// Nothing was received, so there is no fallback to broadcast and the library closed the log.
		Assert.True(session.IsClosed);
		Assert.Empty(store.GetOpen());
		Assert.True(Assert.Single(store.GetAll()).IsClosed);
		Assert.Empty(session.Load());
	}

	[Fact]
	public void InputsSeenArePersistedPerSession()
	{
		string directory = NewDirectory();
		var store = new PayjoinSessionStore(directory, Network.Main);
		PayjoinSession a = store.Create("Wallet", Address);
		a.Persist();
		PayjoinSession b = store.Create("Wallet", Address);
		b.Persist();
		OutPoint outpoint = BitcoinFactory.CreateOutPoint();

		// The same session may look at its inputs again after a retry; another session may not reuse them.
		Assert.True(store.TryMarkInputSeen(outpoint, a.Id));
		Assert.True(store.TryMarkInputSeen(outpoint, a.Id));
		Assert.False(store.TryMarkInputSeen(outpoint, b.Id));
		Assert.True(store.TryMarkInputSeen(BitcoinFactory.CreateOutPoint(), b.Id));

		var reopened = new PayjoinSessionStore(directory, Network.Main);
		Assert.True(reopened.TryMarkInputSeen(outpoint, a.Id));
		Assert.False(reopened.TryMarkInputSeen(outpoint, b.Id));

		// A session that revealed nothing gives the inputs back.
		reopened.ReleaseInputsSeen(a.Id);
		Assert.True(reopened.TryMarkInputSeen(outpoint, b.Id));
		Assert.False(new PayjoinSessionStore(directory, Network.Main).TryMarkInputSeen(outpoint, a.Id));

		// Claims of sessions that are gone for good go with them.
		OutPoint forgotten = BitcoinFactory.CreateOutPoint();
		Assert.True(reopened.TryMarkInputSeen(forgotten, "gone"));
		Assert.True(new PayjoinSessionStore(directory, Network.Main).TryMarkInputSeen(forgotten, a.Id));
	}

	[Fact]
	public void ClosingDropsTheEventLogAndKeepsTheRecord()
	{
		string directory = NewDirectory();
		var store = new PayjoinSessionStore(directory, Network.Main);
		PayjoinSession session = store.Create("Wallet", Address);
		session.PjUri = PayjoinTestVectors.CreateReceiverUri(session.Address, null, session);
		session.Persist();
		Assert.Single(Directory.EnumerateFiles(directory, "*.events"));

		store.RequestCancel(session.Id);
		Assert.True(session.CancelRequested);
		session.Close();

		Assert.Empty(Directory.EnumerateFiles(directory, "*.events"));
		Assert.Empty(Directory.EnumerateFiles(directory, "*.meta"));
		Assert.Empty(Directory.EnumerateFiles(directory, "*.cancel"));
		PayjoinSession closed = Assert.Single(store.GetAll());
		Assert.True(closed.IsClosed);
		Assert.False(closed.CancelRequested);

		// A log left behind without a session record is swept at startup.
		File.WriteAllText(Path.Combine(directory, "orphan.events"), "{}\n");
		_ = new PayjoinSessionStore(directory, Network.Main);
		Assert.Empty(Directory.EnumerateFiles(directory, "*.events"));
	}

	[Fact]
	public void ClosedRecordWinsOverLeftoverOpenRecord()
	{
		string directory = NewDirectory();
		var store = new PayjoinSessionStore(directory, Network.Main);
		PayjoinSession session = store.Create("Wallet", Address);
		session.Persist();
		File.Copy(Path.Combine(directory, $"{session.Id}.meta"), Path.Combine(directory, $"{session.Id}.closed"));

		var reopened = new PayjoinSessionStore(directory, Network.Main);
		Assert.Empty(reopened.GetOpen());
		Assert.True(Assert.Single(reopened.GetAll()).IsClosed);
		Assert.Empty(Directory.EnumerateFiles(directory, "*.meta"));
	}

	[Fact]
	public void SessionInterruptedMidWriteIsStillFound()
	{
		string directory = NewDirectory();
		var store = new PayjoinSessionStore(directory, Network.Main);
		PayjoinSession session = store.Create("Wallet", Address);
		session.PjUri = PayjoinTestVectors.CreateReceiverUri(session.Address, null, session);
		session.Persist();

		// A crash between the two renames of the safe write leaves only the .old and .new copies behind.
		string metaPath = Path.Combine(directory, $"{session.Id}.meta");
		File.Copy(metaPath, metaPath + ".new");
		File.Move(metaPath, metaPath + ".old");

		var reopened = new PayjoinSessionStore(directory, Network.Main);
		Assert.Equal(session.Id, Assert.Single(reopened.GetOpen()).Id);
		Assert.Single(Directory.EnumerateFiles(directory, "*.events"));
	}

	[Fact]
	public void UnreadableFilesAreSkipped()
	{
		string directory = NewDirectory();
		var store = new PayjoinSessionStore(directory, Network.Main);
		PayjoinSession session = store.Create("Wallet", Address);
		session.Persist();
		File.WriteAllText(Path.Combine(directory, "torn.meta"), "Wallet=Wallet\n");
		File.WriteAllText(Path.Combine(directory, "halfwritten.meta.new"), "Wallet=Wallet\n");
		File.WriteAllText(Path.Combine(directory, "inputs-seen.txt"), "garbage\n");
		File.WriteAllText(Path.Combine(directory, "inputs-seen.txt.new"), "garbage\n");

		var reopened = new PayjoinSessionStore(directory, Network.Main);
		Assert.Equal(session.Id, Assert.Single(reopened.GetAll()).Id);
		Assert.False(File.Exists(Path.Combine(directory, "halfwritten.meta.new")));
		Assert.Throws<InvalidOperationException>(() => reopened.RequestCancel("halfwritten"));
	}
}
