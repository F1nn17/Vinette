using System.Collections.Concurrent;
using Vinette.Core;
using Vinette.Core.Mail;
using Vinette.TestSupport;

namespace Vinette.Mail.Tests;

/// <summary>
/// What a watch of the inbox finds out when it is opened again after a lost connection (VIN-086), on a real Dovecot, in each of
/// the three ways the server may be able to tell: QRESYNC (it names what vanished and what changed), CONDSTORE only (it names what
/// changed, removals are counted) and neither (arrivals are found by UID and the folder is called stale).
/// </summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class MailboxCatchUpIntegrationTests(DovecotFixture dovecot)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static DeltaSync ModeOf(string name) => Enum.Parse<DeltaSync>(name);

    private ImapMailSessionFactory Factory(DeltaSync mode) =>
        new(new ImapConnectionSettings(dovecot.Host, dovecot.ImapPort, ConnectionSecurity.None, TimeSpan.FromSeconds(30))) { DeltaSync = mode };

    private async Task<MailAccount> AccountAsync(int messages)
    {
        var account = await dovecot.CreateAccountAsync($"cat{Guid.NewGuid():N}"[..14], Token);
        for (var i = 0; i < messages; i++)
        {
            await dovecot.SaveFixtureAsync(account, "INBOX", "01-plain-ascii.eml", Token);
        }

        return account;
    }

    private sealed class Recorder
    {
        public ConcurrentQueue<MailboxEvent> Events { get; } = new();

        public ValueTask Add(MailboxEvent change)
        {
            Events.Enqueue(change);
            return ValueTask.CompletedTask;
        }

        public IEnumerable<T> Of<T>()
            where T : MailboxEvent => Events.OfType<T>();

        public async Task WaitForAsync(Func<Recorder, bool> condition)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            while (!condition(this))
            {
                await Task.Delay(100, timeout.Token);
            }
        }
    }

    /// <summary>Watches for a moment with the state, then lets go of the connection: what a lost connection leaves behind.</summary>
    private async Task WatchAndLetGoAsync(MailAccount account, DeltaSync mode, MailboxWatchState state)
    {
        await using var session = await Factory(mode).OpenAsync(new MailCredentials("s", account.Address, account.Password), Token);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var watching = session.WatchInboxAsync(new Recorder().Add, TimeSpan.FromMinutes(25), state, stop.Token);
        await Task.Delay(1500, Token);
        await stop.CancelAsync();
        await watching.WaitAsync(TimeSpan.FromSeconds(30), Token);
    }

    /// <summary>Opens a connection with the state of the earlier one and lets it settle; returns what it reported.</summary>
    private async Task<Recorder> WatchAgainAsync(MailAccount account, DeltaSync mode, MailboxWatchState state, TimeSpan? settle = null)
    {
        await using var session = await Factory(mode).OpenAsync(new MailCredentials("s", account.Address, account.Password), Token);
        var recorder = new Recorder();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var watching = session.WatchInboxAsync(recorder.Add, TimeSpan.FromMinutes(25), state, stop.Token);
        await Task.Delay(settle ?? TimeSpan.FromSeconds(4), Token);
        await stop.CancelAsync();
        await watching.WaitAsync(TimeSpan.FromSeconds(30), Token);
        return recorder;
    }

    [Theory]
    [InlineData("Full", true)]
    [InlineData("CondStoreOnly", true)]
    [InlineData("None", false)]
    public async Task A_Watch_Leaves_Its_State_Behind(string name, bool hasModSeq)
    {
        var account = await AccountAsync(3);
        var state = new MailboxWatchState();

        await WatchAndLetGoAsync(account, ModeOf(name), state);

        Assert.True(state.Known);
        Assert.Equal(3u, state.LastUid);
        Assert.Equal((3, 3), (state.Total, state.Unread));
        Assert.NotEqual(0u, state.UidValidity);
        Assert.Equal(hasModSeq, state.HighestModSeq is > 0);
    }

    [Theory]
    [InlineData("Full")]
    [InlineData("CondStoreOnly")]
    [InlineData("None")]
    public async Task What_Happened_While_Nobody_Watched_Is_Found_Out_When_The_Watch_Is_Opened_Again(string name)
    {
        var mode = ModeOf(name);
        var account = await AccountAsync(3);
        var state = new MailboxWatchState();
        await WatchAndLetGoAsync(account, mode, state);

        // while nobody watches: mail arrives, one message is marked, one is removed
        await dovecot.SaveFixtureAsync(account, "INBOX", "02-utf8-qp-alternative.eml", Token);
        await dovecot.DoveadmAsync(Token, "flags", "add", "-u", account.Address, "\\Seen \\Flagged Work", "mailbox", "INBOX", "uid", "1");
        await dovecot.DoveadmAsync(Token, "expunge", "-u", account.Address, "mailbox", "INBOX", "uid", "2");

        var recorder = await WatchAgainAsync(account, mode, state);

        var arrived = Assert.Single(recorder.Of<MessageArrived>());
        Assert.Equal(4u, arrived.Message.Id.Uid);
        Assert.Equal(1, Assert.Single(recorder.Of<MessagesRemoved>()).Count);
        var counters = Assert.Single(recorder.Of<FolderCountersChanged>());
        Assert.Equal((3, 2), (counters.Total, counters.Unread)); // 3 + 1 - 1 messages; message 1 read, 2 gone, 3 and 4 unread
        if (mode == DeltaSync.None)
        {
            Assert.Empty(recorder.Of<MessageMarksChanged>()); // nothing says what else changed
            Assert.Single(recorder.Of<FolderStale>());
        }
        else
        {
            var marks = Assert.Single(recorder.Of<MessageMarksChanged>());
            Assert.Equal(1u, marks.Id.Uid);
            Assert.True(marks.Seen);
            Assert.True(marks.Flagged);
            Assert.Equal(["Work"], marks.Labels);
            Assert.Empty(recorder.Of<FolderStale>());
        }

        Assert.Equal(4u, state.LastUid); // and the state moved on
        Assert.Equal((3, 2), (state.Total, state.Unread));
    }

    [Theory]
    [InlineData("Full")]
    [InlineData("CondStoreOnly")]
    public async Task A_Mailbox_That_Did_Not_Change_Reports_Nothing_When_The_Watch_Is_Opened_Again(string name)
    {
        var mode = ModeOf(name);
        var account = await AccountAsync(3);
        var state = new MailboxWatchState();
        await WatchAndLetGoAsync(account, mode, state);

        var recorder = await WatchAgainAsync(account, mode, state);

        Assert.Empty(recorder.Events);
    }

    [Fact]
    public async Task Without_The_Extensions_A_Reconnect_Is_Called_Stale_Even_If_Nothing_Changed_For_Nothing_Says_So()
    {
        var account = await AccountAsync(2);
        var state = new MailboxWatchState();
        await WatchAndLetGoAsync(account, DeltaSync.None, state);

        var recorder = await WatchAgainAsync(account, DeltaSync.None, state);

        Assert.Single(recorder.Events);
        Assert.IsType<FolderStale>(recorder.Events.Single());
    }

    [Theory]
    [InlineData("Full")]
    [InlineData("None")]
    public async Task A_Folder_That_Is_Not_The_Same_Any_More_Carries_Nothing_Over_And_Is_Called_Stale(string name)
    {
        var mode = ModeOf(name);
        var account = await AccountAsync(2);
        var state = new MailboxWatchState();
        await WatchAndLetGoAsync(account, mode, state);
        var realValidity = state.UidValidity;
        state.UidValidity = realValidity + 7; // the folder was recreated meanwhile: its identity changed

        var recorder = await WatchAgainAsync(account, mode, state);

        Assert.Single(recorder.Events);
        Assert.IsType<FolderStale>(recorder.Events.Single());
        Assert.Equal(realValidity, state.UidValidity); // the state now describes the folder as it is
        Assert.Equal((2, 2), (state.Total, state.Unread));
    }

    [Fact]
    public async Task The_Watcher_Keeps_The_State_Between_Connections_And_Reports_What_Came_In_Between()
    {
        var account = await AccountAsync(1);
        await using var proxy = new FlakyProxy(dovecot.Host, dovecot.ImapPort);
        var factory = new ImapMailSessionFactory(new ImapConnectionSettings("127.0.0.1", proxy.Port, ConnectionSecurity.None, TimeSpan.FromSeconds(30)));
        await using var watcher = new MailboxWatcher(
            factory,
            new MailboxWatchSettings(TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(1), 10, TimeSpan.FromMilliseconds(300)));
        var recorder = new Recorder();
        using var _ = watcher.Subscribe(
            account.Address,
            "s",
            _ => ValueTask.FromResult<MailCredentials?>(new MailCredentials("s", account.Address, account.Password)),
            recorder.Add);
        await WaitForAsync(() => watcher.ConnectionCount == 1);
        await Task.Delay(2000, Token);

        // the network between the watcher and the mail server breaks; mail arrives while the watch is blind; it is opened again
        proxy.Refuse(true);
        proxy.CutAll();
        await WaitForAsync(() => watcher.ConnectionCount == 0);
        await dovecot.SaveFixtureAsync(account, "INBOX", "02-utf8-qp-alternative.eml", Token);
        proxy.Refuse(false);

        await recorder.WaitForAsync(r => r.Of<MessageArrived>().Any());
        Assert.Equal(2u, recorder.Of<MessageArrived>().Single().Message.Id.Uid);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        while (!condition())
        {
            await Task.Delay(100, timeout.Token);
        }
    }
}
