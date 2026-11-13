using Vinette.Core.Mail;

namespace Vinette.Mail.Tests;

/// <summary>The watching connections (VIN-081): one per mailbox, shared, closed after a grace, opened again when lost. No mail server needed.</summary>
public sealed class MailboxWatcherTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class FakeSession : IMailSession
    {
        private readonly TaskCompletionSource<Exception?> end = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource watching = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Func<MailboxEvent, ValueTask>? deliver;

        public bool IsConnected { get; private set; } = true;

        public bool Disposed { get; private set; }

        public Task Watching => watching.Task;

        /// <summary>The mail server reports a change.</summary>
        public ValueTask Raise(MailboxEvent change) => deliver!(change);

        /// <summary>The connection breaks.</summary>
        public void Break() => end.TrySetResult(new IOException("connection lost"));

        public async Task WatchInboxAsync(Func<MailboxEvent, ValueTask> onChange, TimeSpan restartAfter, MailboxWatchState state, CancellationToken cancellationToken)
        {
            deliver = onChange;
            watching.TrySetResult();
            using var registration = cancellationToken.Register(() => end.TrySetCanceled(cancellationToken));
            if (await end.Task.ConfigureAwait(false) is { } failure)
            {
                throw failure;
            }
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            IsConnected = false;
            return ValueTask.CompletedTask;
        }

        public Task PingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<MailFolderInfo>> GetFoldersAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MessagePage> ListMessagesAsync(MessageListQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MessageSource> GetMessageAsync(MessageId id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MessageFlagState> UpdateFlagsAsync(MessageId id, MessageFlagChange change, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MessageId?> MoveMessageAsync(MessageId id, string targetFolder, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<MovedMessage>> MoveMessagesAsync(IReadOnlyList<MessageId> ids, string targetFolder, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<MessageId>> UpdateFlagsOfManyAsync(IReadOnlyList<MessageId> ids, MessageFlagChange change, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteMessagePermanentlyAsync(MessageId id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> DeleteMessagesPermanentlyAsync(IReadOnlyList<MessageId> ids, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> EmptyFolderAsync(string folder, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> GetLabelsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> UpdateMessageLabelsAsync(MessageId id, IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<LabeledMessages> ListLabeledMessagesAsync(string label, int limit, bool includePreview, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> RenameLabelAsync(string oldName, string? newName, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MailQuota?> GetQuotaAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeFactory : IMailSessionFactory
    {
        private readonly Lock gate = new();

        public List<FakeSession> Sessions { get; } = [];

        public List<string> Addresses { get; } = [];

        /// <summary>Addresses whose password the server rejects.</summary>
        public HashSet<string> Rejected { get; } = [];

        public Task<IMailSession> OpenAsync(MailCredentials credentials, CancellationToken cancellationToken)
        {
            if (Rejected.Contains(credentials.Password))
            {
                throw new MailAccessRevokedException("rejected");
            }

            var session = new FakeSession();
            lock (gate)
            {
                Sessions.Add(session);
                Addresses.Add(credentials.Address);
            }

            return Task.FromResult<IMailSession>(session);
        }

        public int Opened
        {
            get
            {
                lock (gate)
                {
                    return Sessions.Count;
                }
            }
        }

        public FakeSession Last
        {
            get
            {
                lock (gate)
                {
                    return Sessions[^1];
                }
            }
        }
    }

    private static readonly MailboxWatchSettings Quick = new(TimeSpan.FromMinutes(25), TimeSpan.FromMilliseconds(150), 10, TimeSpan.FromMilliseconds(50));

    private static Func<CancellationToken, ValueTask<MailCredentials?>> Credentials(string address, string session, string password = "pw") =>
        _ => ValueTask.FromResult<MailCredentials?>(new MailCredentials(session, address, password));

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static readonly MailboxEvent Arrived = new FolderCountersChanged("INBOX", 1, 1);

    [Fact]
    public async Task The_First_Listener_Opens_A_Watching_Connection_And_Gets_What_The_Server_Reports()
    {
        var factory = new FakeFactory();
        await using var watcher = new MailboxWatcher(factory, Quick);
        var heard = new List<MailboxEvent>();

        using var _ = watcher.Subscribe("a@x.test", "s1", Credentials("a@x.test", "s1"), c =>
        {
            lock (heard)
            {
                heard.Add(c);
            }

            return ValueTask.CompletedTask;
        });
        await UntilAsync(() => factory.Opened == 1);
        await factory.Last.Watching.WaitAsync(Token);
        await factory.Last.Raise(Arrived);

        Assert.Equal([Arrived], heard);
        Assert.Equal(1, watcher.ConnectionCount);
        Assert.Equal(1, watcher.WatchCount);
    }

    [Fact]
    public async Task Two_Listeners_Of_One_Mailbox_Share_One_Connection()
    {
        var factory = new FakeFactory();
        await using var watcher = new MailboxWatcher(factory, Quick);
        var first = 0;
        var second = 0;

        using var a = watcher.Subscribe("a@x.test", "s1", Credentials("a@x.test", "s1"), _ => { Interlocked.Increment(ref first); return ValueTask.CompletedTask; });
        using var b = watcher.Subscribe("A@X.test", "s2", Credentials("a@x.test", "s2"), _ => { Interlocked.Increment(ref second); return ValueTask.CompletedTask; });
        await UntilAsync(() => factory.Opened == 1);
        await factory.Last.Watching.WaitAsync(Token);
        await factory.Last.Raise(Arrived);

        Assert.Equal(1, factory.Opened);
        Assert.Equal((1, 1), (first, second));
    }

    [Fact]
    public async Task Two_Mailboxes_Have_A_Connection_Each()
    {
        var factory = new FakeFactory();
        await using var watcher = new MailboxWatcher(factory, Quick);

        using var a = watcher.Subscribe("a@x.test", "s1", Credentials("a@x.test", "s1"), _ => ValueTask.CompletedTask);
        using var b = watcher.Subscribe("b@x.test", "s2", Credentials("b@x.test", "s2"), _ => ValueTask.CompletedTask);
        await UntilAsync(() => factory.Opened == 2);

        Assert.Equal(["a@x.test", "b@x.test"], factory.Addresses.Order());
        Assert.Equal(2, watcher.WatchCount);
    }

    [Fact]
    public async Task The_Connection_Closes_After_The_Last_Listener_Has_Gone_For_The_Grace()
    {
        var factory = new FakeFactory();
        await using var watcher = new MailboxWatcher(factory, Quick);
        var subscription = watcher.Subscribe("a@x.test", "s1", Credentials("a@x.test", "s1"), _ => ValueTask.CompletedTask);
        await UntilAsync(() => factory.Opened == 1);
        await factory.Last.Watching.WaitAsync(Token);

        subscription.Dispose();
        subscription.Dispose(); // twice is harmless
        Assert.False(factory.Last.Disposed); // not at once
        await UntilAsync(() => factory.Last.Disposed);

        await UntilAsync(() => watcher.WatchCount == 0 && watcher.ConnectionCount == 0);
    }

    [Fact]
    public async Task A_Listener_Who_Comes_Back_Within_The_Grace_Finds_The_Same_Connection()
    {
        var factory = new FakeFactory();
        var settings = Quick with { Grace = TimeSpan.FromSeconds(30) };
        await using var watcher = new MailboxWatcher(factory, settings);
        var first = watcher.Subscribe("a@x.test", "s1", Credentials("a@x.test", "s1"), _ => ValueTask.CompletedTask);
        await UntilAsync(() => factory.Opened == 1);
        await factory.Last.Watching.WaitAsync(Token);

        first.Dispose(); // a page reload
        using var second = watcher.Subscribe("a@x.test", "s1", Credentials("a@x.test", "s1"), _ => ValueTask.CompletedTask);
        await Task.Delay(100, Token);

        Assert.Equal(1, factory.Opened);
        Assert.False(factory.Sessions[0].Disposed);
    }

    [Fact]
    public async Task A_Lost_Connection_Is_Opened_Again_And_The_Password_Is_Asked_For_Again()
    {
        var factory = new FakeFactory();
        await using var watcher = new MailboxWatcher(factory, Quick);
        var asked = 0;
        var heard = 0;
        using var _ = watcher.Subscribe(
            "a@x.test",
            "s1",
            _ =>
            {
                Interlocked.Increment(ref asked);
                return ValueTask.FromResult<MailCredentials?>(new MailCredentials("s1", "a@x.test", "pw"));
            },
            _ => { Interlocked.Increment(ref heard); return ValueTask.CompletedTask; });
        await UntilAsync(() => factory.Opened == 1);
        await factory.Sessions[0].Watching.WaitAsync(Token);

        factory.Sessions[0].Break();
        await UntilAsync(() => factory.Opened == 2);
        await factory.Last.Watching.WaitAsync(Token);
        await factory.Last.Raise(Arrived);

        Assert.True(factory.Sessions[0].Disposed);
        Assert.Equal(2, asked);
        Assert.Equal(1, heard);
        Assert.Equal(1, watcher.ConnectionCount);
    }

    [Fact]
    public async Task A_Listener_Whose_Session_Has_Ended_Is_Dropped_And_Nothing_Is_Opened_For_It()
    {
        var factory = new FakeFactory();
        await using var watcher = new MailboxWatcher(factory, Quick);

        using var _ = watcher.Subscribe("a@x.test", "s1", _ => ValueTask.FromResult<MailCredentials?>(null), _ => ValueTask.CompletedTask);
        await UntilAsync(() => watcher.WatchCount == 0);

        Assert.Equal(0, factory.Opened);
    }

    [Fact]
    public async Task A_Rejected_Password_Drops_Its_Listener_And_The_Next_One_Is_Used()
    {
        var factory = new FakeFactory();
        factory.Rejected.Add("old");
        await using var watcher = new MailboxWatcher(factory, Quick);
        var heardByNew = 0;

        using var stale = watcher.Subscribe("a@x.test", "s1", Credentials("a@x.test", "s1", "old"), _ => ValueTask.CompletedTask);
        using var fresh = watcher.Subscribe("a@x.test", "s2", Credentials("a@x.test", "s2", "new"), _ => { Interlocked.Increment(ref heardByNew); return ValueTask.CompletedTask; });
        await UntilAsync(() => factory.Opened == 1);
        await factory.Last.Watching.WaitAsync(Token);
        await factory.Last.Raise(Arrived);

        Assert.Equal(1, heardByNew);
    }

    [Fact]
    public async Task A_Listener_That_Fails_Does_Not_Stop_The_Others_Nor_The_Watch()
    {
        var factory = new FakeFactory();
        await using var watcher = new MailboxWatcher(factory, Quick);
        var heard = 0;
        using var bad = watcher.Subscribe("a@x.test", "s1", Credentials("a@x.test", "s1"), _ => throw new InvalidOperationException("boom"));
        using var good = watcher.Subscribe("a@x.test", "s2", Credentials("a@x.test", "s2"), _ => { Interlocked.Increment(ref heard); return ValueTask.CompletedTask; });
        await UntilAsync(() => factory.Opened == 1);
        await factory.Last.Watching.WaitAsync(Token);

        await factory.Last.Raise(Arrived);
        await factory.Last.Raise(Arrived);

        Assert.Equal(2, heard);
        Assert.Equal(1, factory.Opened);
    }

    [Fact]
    public async Task Only_As_Many_Connections_Are_Open_As_The_Limit_Allows_And_A_Waiting_Mailbox_Gets_One_When_A_Place_Is_Free()
    {
        var factory = new FakeFactory();
        await using var watcher = new MailboxWatcher(factory, Quick with { MaxConnections = 1 });
        var first = watcher.Subscribe("a@x.test", "s1", Credentials("a@x.test", "s1"), _ => ValueTask.CompletedTask);
        await UntilAsync(() => factory.Opened == 1);
        using var second = watcher.Subscribe("b@x.test", "s2", Credentials("b@x.test", "s2"), _ => ValueTask.CompletedTask);
        await Task.Delay(200, Token);
        Assert.Equal(1, factory.Opened); // no free place

        first.Dispose();
        await UntilAsync(() => factory.Opened == 2);

        Assert.Equal(["a@x.test", "b@x.test"], factory.Addresses);
    }

    [Fact]
    public async Task Disposing_The_Watcher_Closes_Every_Connection()
    {
        var factory = new FakeFactory();
        var watcher = new MailboxWatcher(factory, Quick);
        using var a = watcher.Subscribe("a@x.test", "s1", Credentials("a@x.test", "s1"), _ => ValueTask.CompletedTask);
        using var b = watcher.Subscribe("b@x.test", "s2", Credentials("b@x.test", "s2"), _ => ValueTask.CompletedTask);
        await UntilAsync(() => factory.Opened == 2);
        await factory.Sessions[0].Watching.WaitAsync(Token);
        await factory.Sessions[1].Watching.WaitAsync(Token);

        await watcher.DisposeAsync();

        Assert.All(factory.Sessions, s => Assert.True(s.Disposed));
        Assert.Equal(0, watcher.ConnectionCount);
        Assert.Throws<ObjectDisposedException>(() => watcher.Subscribe("c@x.test", "s3", Credentials("c@x.test", "s3"), _ => ValueTask.CompletedTask));
    }
}
