using Vinette.Core.Mail;

namespace Vinette.Mail.Tests;

/// <summary>Ceilings on open mail connections (VIN-034), against a fake mail server.</summary>
public sealed class MailConnectionLimitsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static MailCredentials Credentials(string session, string address = "alice@vinette.test") => new(session, address, "pw");

    private sealed class FakeSession : IMailSession
    {
        public bool IsConnected { get; set; } = true;

        public Task PingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<MailFolderInfo>> GetFoldersAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MailFolderInfo>>([]);


        public Task<MailQuota?> GetQuotaAsync(CancellationToken cancellationToken) => Task.FromResult<MailQuota?>(null);

        public Task<MessageSource> GetMessageAsync(MessageId id, CancellationToken cancellationToken) => throw new NotSupportedException();



        public Task<MessagePage> ListMessagesAsync(MessageListQuery query, CancellationToken cancellationToken) => Task.FromResult(new MessagePage(1, [], null, 0));

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeFactory : IMailSessionFactory
    {
        private int opened;

        public List<FakeSession> Sessions { get; } = [];

        public Exception? FailWith { get; set; }

        public int Opened => Volatile.Read(ref opened);

        public Task<IMailSession> OpenAsync(MailCredentials credentials, CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref opened);
            if (FailWith is not null)
            {
                throw FailWith;
            }

            var session = new FakeSession();
            lock (Sessions)
            {
                Sessions.Add(session);
            }

            return Task.FromResult<IMailSession>(session);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
    }

    private static MailConnectionManager Manager(IMailSessionFactory factory, int perUser, int total, int waitMs = 100, TimeProvider? clock = null) =>
        new(factory, new MailReconnectPolicy(0, TimeSpan.Zero, 1, 0), clock, new MailConnectionLimits(perUser, total, TimeSpan.FromMilliseconds(waitMs)));

    private static Task<int> UseAsync(MailConnectionManager manager, string session, string address = "alice@vinette.test") =>
        manager.ExecuteAsync(Credentials(session, address), (_, _) => Task.FromResult(1), Token);

    [Fact]
    public async Task A_Mailbox_Over_Its_Limit_Gets_A_Busy_Error_After_Waiting()
    {
        var factory = new FakeFactory();
        await using var manager = Manager(factory, perUser: 1, total: 10);
        await UseAsync(manager, "phone");

        var busy = await Assert.ThrowsAsync<MailBusyException>(() => UseAsync(manager, "laptop"));

        Assert.True(busy.RetryAfter > TimeSpan.Zero);
        Assert.Equal(1, factory.Opened);
        Assert.Equal(1, await UseAsync(manager, "phone")); // the first session is unaffected
    }

    [Fact]
    public async Task The_Per_Mailbox_Limit_Does_Not_Touch_Other_Mailboxes()
    {
        var factory = new FakeFactory();
        await using var manager = Manager(factory, perUser: 1, total: 10);
        await UseAsync(manager, "a", "alice@vinette.test");

        await UseAsync(manager, "b", "bob@vinette.test");

        Assert.Equal(2, factory.Opened);
    }

    [Fact]
    public async Task The_Global_Limit_Applies_To_All_Mailboxes()
    {
        var factory = new FakeFactory();
        await using var manager = Manager(factory, perUser: 5, total: 2);
        await UseAsync(manager, "a", "alice@vinette.test");
        await UseAsync(manager, "b", "bob@vinette.test");

        await Assert.ThrowsAsync<MailBusyException>(() => UseAsync(manager, "c", "carol@vinette.test"));

        Assert.Equal(2, factory.Opened);
    }

    [Fact]
    public async Task A_Failed_Global_Wait_Gives_The_User_Place_Back()
    {
        var factory = new FakeFactory();
        await using var manager = Manager(factory, perUser: 1, total: 1);
        await UseAsync(manager, "a", "alice@vinette.test");
        await Assert.ThrowsAsync<MailBusyException>(() => UseAsync(manager, "b", "bob@vinette.test"));

        await manager.ReleaseAsync("a", Token);

        Assert.Equal(1, await UseAsync(manager, "b", "bob@vinette.test")); // bob's own place was not lost
    }

    [Fact]
    public async Task A_Waiting_Request_Gets_The_Place_When_Another_Connection_Closes()
    {
        var factory = new FakeFactory();
        await using var manager = Manager(factory, perUser: 1, total: 10, waitMs: 5000);
        await UseAsync(manager, "phone");

        var waiting = UseAsync(manager, "laptop");
        await Task.Delay(50, Token);
        Assert.False(waiting.IsCompleted);
        await manager.ReleaseAsync("phone", Token);

        Assert.Equal(1, await waiting);
        Assert.Equal(2, factory.Opened);
    }

    [Fact]
    public async Task A_Failed_Open_Gives_Its_Place_Back()
    {
        var factory = new FakeFactory { FailWith = new MailUnavailableException("down") };
        await using var manager = Manager(factory, perUser: 1, total: 1);
        await Assert.ThrowsAsync<MailUnavailableException>(() => UseAsync(manager, "a"));

        factory.FailWith = null;

        Assert.Equal(1, await UseAsync(manager, "a"));
    }

    [Fact]
    public async Task Replacing_A_Dead_Connection_Reuses_The_Same_Place()
    {
        var factory = new FakeFactory();
        await using var manager = Manager(factory, perUser: 1, total: 1);
        await UseAsync(manager, "a");
        factory.Sessions[0].IsConnected = false;

        Assert.Equal(1, await UseAsync(manager, "a"));
        Assert.Equal(2, factory.Opened);
    }

    [Fact]
    public async Task Closing_An_Idle_Connection_Gives_Its_Place_Back()
    {
        var factory = new FakeFactory();
        var clock = new ManualTimeProvider();
        await using var manager = Manager(factory, perUser: 1, total: 1, clock: clock);
        await UseAsync(manager, "a");
        clock.Advance(TimeSpan.FromMinutes(10));
        _ = await manager.CloseIdleAsync(TimeSpan.FromMinutes(5), Token);

        Assert.Equal(1, await UseAsync(manager, "b", "bob@vinette.test"));
    }

    [Fact]
    public async Task A_Hundred_Parallel_Requests_Over_A_Small_Limit_Never_Exceed_It()
    {
        var factory = new FakeFactory();
        await using var manager = Manager(factory, perUser: 100, total: 3, waitMs: 30_000);
        var open = 0;
        var peak = 0;

        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(
            () => manager.ExecuteAsync<int>(
                Credentials($"s{i}", $"user{i}@vinette.test"),
                async (unused, token) =>
                {
                    var now = Interlocked.Increment(ref open);
                    lock (factory)
                    {
                        peak = Math.Max(peak, now);
                    }

                    await Task.Delay(5, token);
                    _ = Interlocked.Decrement(ref open); // the result is not needed

                    // The place is held until the connection closes, so close it to let the next session in.
                    return 0;
                },
                Token)
                .ContinueWith(_ => manager.ReleaseAsync($"s{i}", Token), Token).Unwrap(), Token)));

        Assert.True(peak <= 3, $"{peak} connections were in use at once");
    }
}
