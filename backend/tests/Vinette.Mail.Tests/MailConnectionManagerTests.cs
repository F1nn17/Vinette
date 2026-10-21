using Vinette.Core.Mail;

namespace Vinette.Mail.Tests;

/// <summary>The connection manager against a fake mail server: no network, no Docker.</summary>
public sealed class MailConnectionManagerTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        private long ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
    }

    private static MailConnectionManager NewManager(IMailSessionFactory factory, int maxRetries = 3) =>
        new(factory, new MailReconnectPolicy(maxRetries, TimeSpan.Zero, 1, 0));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static MailCredentials Credentials(string session = "s1", string address = "alice@vinette.test") =>
        new(session, address, "pw");

    private sealed class FakeSession : IMailSession
    {
        private int running;

        public bool IsConnected { get; set; } = true;

        public bool Disposed { get; private set; }

        public int MaxRunning { get; private set; }

        public Task PingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public int Enter()
        {
            var now = Interlocked.Increment(ref running);
            MaxRunning = Math.Max(MaxRunning, now);
            return now;
        }

        public void Leave() => Interlocked.Decrement(ref running);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeFactory : IMailSessionFactory
    {
        private int opened;

        public List<FakeSession> Sessions { get; } = [];

        public Exception? FailWith { get; set; }

        public TimeSpan OpenDelay { get; set; }

        /// <summary>How many opens fail with <see cref="FailWith"/> before they succeed (0 = fail every time).</summary>
        public int FailTimes { get; set; }

        public int Opened => Volatile.Read(ref opened);

        public async Task<IMailSession> OpenAsync(MailCredentials credentials, CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref opened);
            if (OpenDelay > TimeSpan.Zero)
            {
                await Task.Delay(OpenDelay, cancellationToken);
            }

            if (FailWith is not null && (FailTimes == 0 || Opened <= FailTimes))
            {
                throw FailWith;
            }

            var session = new FakeSession();
            lock (Sessions)
            {
                Sessions.Add(session);
            }

            return session;
        }
    }

    private static async Task<int> SlowCommandAsync(IMailSession session, CancellationToken token)
    {
        var fake = (FakeSession)session;
        _ = fake.Enter();
        try
        {
            await Task.Delay(20, token);
        }
        finally
        {
            fake.Leave();
        }

        return 1;
    }

    /// <summary>A command that signals it started and then waits until the test lets it finish.</summary>
    private sealed class BlockedCommand
    {
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => started.Task;

        public void Release() => release.SetResult();

        public async Task<int> RunAsync(IMailSession session, CancellationToken token)
        {
            started.SetResult();
            await release.Task;
            return 0;
        }
    }

    [Fact]
    public async Task Parallel_Requests_Of_One_Session_Share_One_Connection_And_Never_Overlap()
    {
        var factory = new FakeFactory { OpenDelay = TimeSpan.FromMilliseconds(50) };
        await using var manager = NewManager(factory);

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => manager.ExecuteAsync(Credentials(), SlowCommandAsync, Token)));

        Assert.Equal(1, factory.Opened);
        Assert.Equal(1, factory.Sessions.Single().MaxRunning);
        Assert.Equal(1, manager.ConnectionCount);
    }

    [Fact]
    public async Task Different_Sessions_Get_Their_Own_Connections()
    {
        var factory = new FakeFactory();
        await using var manager = NewManager(factory);

        await manager.ExecuteAsync(Credentials("s1"), SlowCommandAsync, Token);
        await manager.ExecuteAsync(Credentials("s2", "bob@vinette.test"), SlowCommandAsync, Token);

        Assert.Equal(2, factory.Opened);
        Assert.Equal(2, manager.ConnectionCount);
    }

    [Fact]
    public async Task Different_Sessions_Do_Not_Wait_For_Each_Other()
    {
        await using var manager = NewManager(new FakeFactory());
        var blocked = new BlockedCommand();
        var first = manager.ExecuteAsync(Credentials("s1"), blocked.RunAsync, Token);
        await blocked.Started;

        var other = await manager.ExecuteAsync(Credentials("s2"), (_, _) => Task.FromResult(42), Token);

        Assert.Equal(42, other);
        blocked.Release();
        await first;
    }

    [Fact]
    public async Task A_Dead_Connection_Is_Replaced_On_The_Next_Request()
    {
        var factory = new FakeFactory();
        await using var manager = NewManager(factory);
        await manager.ExecuteAsync(Credentials(), SlowCommandAsync, Token);
        factory.Sessions[0].IsConnected = false;

        await manager.ExecuteAsync(Credentials(), SlowCommandAsync, Token);

        Assert.Equal(2, factory.Opened);
        Assert.True(factory.Sessions[0].Disposed);
    }

    [Fact]
    public async Task A_Failed_Open_Is_Retried_And_Then_Reported_As_Unavailable_Without_Being_Remembered()
    {
        var factory = new FakeFactory { FailWith = new MailUnavailableException("down") };
        await using var manager = NewManager(factory);

        await Assert.ThrowsAsync<MailUnavailableException>(() => manager.ExecuteAsync(Credentials(), SlowCommandAsync, Token));
        Assert.Equal(4, factory.Opened); // the first try and three retries

        factory.FailWith = null;
        var result = await manager.ExecuteAsync(Credentials(), SlowCommandAsync, Token);

        Assert.Equal(1, result);
        Assert.Equal(5, factory.Opened);
    }

    [Fact]
    public async Task A_Short_Outage_While_Connecting_Is_Invisible_To_The_Request()
    {
        var factory = new FakeFactory { FailWith = new MailUnavailableException("down"), FailTimes = 2 };
        await using var manager = NewManager(factory);

        var result = await manager.ExecuteAsync(Credentials(), SlowCommandAsync, Token);

        Assert.Equal(1, result);
        Assert.Equal(3, factory.Opened);
    }

    [Fact]
    public async Task A_Connection_Lost_During_A_Command_Is_Replaced_And_The_Command_Runs_Again()
    {
        var factory = new FakeFactory();
        await using var manager = NewManager(factory);
        var calls = 0;

        var result = await manager.ExecuteAsync(Credentials(), (session, _) =>
        {
            calls++;
            if (calls == 1)
            {
                ((FakeSession)session).IsConnected = false;
                throw new IOException("connection reset");
            }

            return Task.FromResult(7);
        }, Token);

        Assert.Equal(7, result);
        Assert.Equal(2, calls);
        Assert.Equal(2, factory.Opened);
        Assert.True(factory.Sessions[0].Disposed);
    }

    [Fact]
    public async Task A_Command_That_Must_Not_Run_Twice_Is_Not_Repeated_After_A_Lost_Connection()
    {
        var factory = new FakeFactory();
        await using var manager = NewManager(factory);
        var calls = 0;

        await Assert.ThrowsAsync<MailUnavailableException>(() => manager.ExecuteAsync<int>(
            Credentials(),
            (_, _) =>
            {
                calls++;
                throw new IOException("connection reset");
            },
            Token,
            safeToRepeat: false));

        Assert.Equal(1, calls);
        Assert.True(factory.Sessions[0].Disposed);
        Assert.Equal(1, await manager.ExecuteAsync(Credentials(), SlowCommandAsync, Token)); // the next command reconnects
    }

    [Fact]
    public async Task A_Command_Keeps_Failing_After_Reconnects_Until_The_Retries_Are_Used_Up()
    {
        var factory = new FakeFactory();
        await using var manager = NewManager(factory, maxRetries: 2);
        var calls = 0;

        await Assert.ThrowsAsync<MailUnavailableException>(() => manager.ExecuteAsync<int>(
            Credentials(),
            (_, _) =>
            {
                calls++;
                throw new IOException("connection reset");
            },
            Token));

        Assert.Equal(3, calls);
        Assert.Equal(3, factory.Opened);
    }

    [Fact]
    public async Task A_Password_Rejected_After_Reconnecting_Is_Reported_At_Once_Without_Retries()
    {
        var factory = new FakeFactory();
        await using var manager = NewManager(factory);
        await manager.ExecuteAsync(Credentials(), SlowCommandAsync, Token);
        factory.Sessions[0].IsConnected = false;
        factory.FailWith = new MailAccessRevokedException("password changed");

        await Assert.ThrowsAsync<MailAccessRevokedException>(() => manager.ExecuteAsync(Credentials(), SlowCommandAsync, Token));

        Assert.Equal(2, factory.Opened); // no retries: more wrong passwords could lock the mailbox
        Assert.True(factory.Sessions[0].Disposed);
    }

    [Fact]
    public void The_Pauses_Between_Retries_Grow()
    {
        var policy = new MailReconnectPolicy(3, TimeSpan.FromMilliseconds(200), 3, 0);

        Assert.Equal(TimeSpan.FromMilliseconds(200), policy.DelayBefore(1));
        Assert.Equal(TimeSpan.FromMilliseconds(600), policy.DelayBefore(2));
        Assert.Equal(TimeSpan.FromMilliseconds(1800), policy.DelayBefore(3));
    }

    [Fact]
    public void The_Pauses_Are_Spread_By_The_Jitter()
    {
        var policy = MailReconnectPolicy.Default;

        var delays = Enumerable.Range(0, 50).Select(_ => policy.DelayBefore(1).TotalMilliseconds).ToList();

        Assert.All(delays, d => Assert.InRange(d, 160, 240));
        Assert.True(delays.Distinct().Count() > 1);
    }

    [Fact]
    public async Task A_Failing_Command_Does_Not_Block_The_Next_One()
    {
        var factory = new FakeFactory();
        await using var manager = NewManager(factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.ExecuteAsync<int>(Credentials(), (_, _) => throw new InvalidOperationException("boom"), Token));
        var result = await manager.ExecuteAsync(Credentials(), SlowCommandAsync, Token);

        Assert.Equal(1, result);
        Assert.Equal(1, factory.Opened);
    }

    [Fact]
    public async Task A_Cancelled_Waiter_Leaves_The_Queue_Without_Breaking_It()
    {
        await using var manager = NewManager(new FakeFactory());
        var blocked = new BlockedCommand();
        var first = manager.ExecuteAsync(Credentials(), blocked.RunAsync, Token);
        await blocked.Started;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var waiting = manager.ExecuteAsync(Credentials(), SlowCommandAsync, cancel.Token);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        blocked.Release();
        await first;

        Assert.Equal(1, await manager.ExecuteAsync(Credentials(), SlowCommandAsync, Token));
    }

    [Fact]
    public async Task A_Hundred_Parallel_Requests_Of_Several_Users_Run_Without_Overlap_Or_Deadlock()
    {
        var factory = new FakeFactory { OpenDelay = TimeSpan.FromMilliseconds(5) };
        await using var manager = NewManager(factory);
        var users = new[] { "s1", "s2", "s3", "s4", "s5" };

        var work = Enumerable.Range(0, 100)
            .Select(i => Task.Run(() => manager.ExecuteAsync(Credentials(users[i % users.Length]), SlowCommandAsync, Token), Token))
            .ToArray();
        _ = await Task.WhenAny(Task.WhenAll(work), Task.Delay(TimeSpan.FromSeconds(30), Token));

        Assert.True(work.All(t => t.IsCompletedSuccessfully), "Some requests did not finish: deadlock or failure.");
        Assert.Equal(users.Length, factory.Opened);
        Assert.All(factory.Sessions, s => Assert.Equal(1, s.MaxRunning));
    }

    [Fact]
    public async Task Requests_Of_One_Session_Run_In_The_Order_They_Were_Made()
    {
        await using var manager = NewManager(new FakeFactory());
        var order = new List<int>();
        var blocked = new BlockedCommand();
        var first = manager.ExecuteAsync(Credentials(), blocked.RunAsync, Token);
        await blocked.Started;

        var queued = Enumerable.Range(1, 20).Select(i =>
            manager.ExecuteAsync(Credentials(), (_, _) =>
            {
                order.Add(i);
                return Task.FromResult(i);
            }, Token)).ToArray();
        blocked.Release();
        await first;
        await Task.WhenAll(queued);

        Assert.Equal(Enumerable.Range(1, 20), order);
    }

    [Fact]
    public async Task A_Command_Cancelled_While_Running_Drops_The_Connection_And_The_Next_Command_Reconnects()
    {
        var factory = new FakeFactory();
        await using var manager = NewManager(factory);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var started = new TaskCompletionSource();
        var running = manager.ExecuteAsync<int>(Credentials(), async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        }, cancel.Token);
        await started.Task;

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        Assert.True(factory.Sessions[0].Disposed);
        Assert.Equal(1, await manager.ExecuteAsync(Credentials(), SlowCommandAsync, Token));
        Assert.Equal(2, factory.Opened);
    }

    [Fact]
    public async Task A_Request_Cancelled_While_The_Connection_Opens_Does_Not_Leave_A_Stuck_Queue()
    {
        var factory = new FakeFactory { OpenDelay = TimeSpan.FromSeconds(30) };
        await using var manager = NewManager(factory);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var opening = manager.ExecuteAsync(Credentials(), SlowCommandAsync, cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);

        factory.OpenDelay = TimeSpan.Zero;

        Assert.Equal(1, await manager.ExecuteAsync(Credentials(), SlowCommandAsync, Token));
    }

    [Fact]
    public async Task A_Connection_Idle_For_Long_Enough_Is_Closed_And_Reopened_On_Demand()
    {
        var factory = new FakeFactory();
        var clock = new ManualTimeProvider();
        await using var manager = new MailConnectionManager(factory, MailReconnectPolicy.Default, clock);
        await manager.ExecuteAsync(Credentials(), SlowCommandAsync, Token);
        clock.Advance(TimeSpan.FromMinutes(6));

        var closed = await manager.CloseIdleAsync(TimeSpan.FromMinutes(5), Token);

        Assert.Equal(1, closed);
        Assert.True(factory.Sessions.Single().Disposed);
        Assert.Equal(0, manager.ConnectionCount);
        Assert.Equal(1, await manager.ExecuteAsync(Credentials(), SlowCommandAsync, Token));
        Assert.Equal(2, factory.Opened);
    }

    [Fact]
    public async Task A_Recently_Used_Connection_Stays_Open()
    {
        var factory = new FakeFactory();
        var clock = new ManualTimeProvider();
        await using var manager = new MailConnectionManager(factory, MailReconnectPolicy.Default, clock);
        await manager.ExecuteAsync(Credentials(), SlowCommandAsync, Token);
        clock.Advance(TimeSpan.FromMinutes(4));
        await manager.ExecuteAsync(Credentials(), SlowCommandAsync, Token); // used again: the idle time starts over
        clock.Advance(TimeSpan.FromMinutes(4));

        var closed = await manager.CloseIdleAsync(TimeSpan.FromMinutes(5), Token);

        Assert.Equal(0, closed);
        Assert.False(factory.Sessions.Single().Disposed);
        Assert.Equal(1, manager.ConnectionCount);
    }

    [Fact]
    public async Task A_Connection_With_A_Running_Command_Is_Not_Closed_As_Idle()
    {
        var factory = new FakeFactory();
        var clock = new ManualTimeProvider();
        await using var manager = new MailConnectionManager(factory, MailReconnectPolicy.Default, clock);
        var blocked = new BlockedCommand();
        var running = manager.ExecuteAsync(Credentials(), blocked.RunAsync, Token);
        await blocked.Started;
        clock.Advance(TimeSpan.FromHours(1));

        var closed = await manager.CloseIdleAsync(TimeSpan.FromMinutes(5), Token);

        Assert.Equal(0, closed);
        Assert.False(factory.Sessions.Single().Disposed);
        blocked.Release();
        await running;
    }

    [Fact]
    public async Task Only_The_Idle_Sessions_Are_Closed()
    {
        var factory = new FakeFactory();
        var clock = new ManualTimeProvider();
        await using var manager = new MailConnectionManager(factory, MailReconnectPolicy.Default, clock);
        await manager.ExecuteAsync(Credentials("old"), SlowCommandAsync, Token);
        clock.Advance(TimeSpan.FromMinutes(10));
        await manager.ExecuteAsync(Credentials("new", "bob@vinette.test"), SlowCommandAsync, Token);

        var closed = await manager.CloseIdleAsync(TimeSpan.FromMinutes(5), Token);

        Assert.Equal(1, closed);
        Assert.True(factory.Sessions[0].Disposed);
        Assert.False(factory.Sessions[1].Disposed);
        Assert.Equal(1, manager.ConnectionCount);
    }

    [Fact]
    public async Task Releasing_A_Session_Closes_Its_Connection()
    {
        var factory = new FakeFactory();
        await using var manager = NewManager(factory);
        await manager.ExecuteAsync(Credentials(), SlowCommandAsync, Token);

        await manager.ReleaseAsync("s1", Token);

        Assert.True(factory.Sessions.Single().Disposed);
        Assert.Equal(0, manager.ConnectionCount);
    }

    [Fact]
    public async Task Releasing_Waits_For_The_Running_Command()
    {
        var factory = new FakeFactory();
        await using var manager = NewManager(factory);
        var blocked = new BlockedCommand();
        var running = manager.ExecuteAsync(Credentials(), blocked.RunAsync, Token);
        await blocked.Started;

        var releasing = manager.ReleaseAsync("s1", Token);
        await Task.Delay(50, Token);
        Assert.False(factory.Sessions.Single().Disposed);
        blocked.Release();
        await running;
        await releasing;

        Assert.True(factory.Sessions.Single().Disposed);
    }

    [Fact]
    public async Task A_Request_Queued_Before_The_Release_Still_Runs_And_A_Later_One_Opens_A_Fresh_Connection()
    {
        var factory = new FakeFactory();
        await using var manager = NewManager(factory);
        var blocked = new BlockedCommand();
        var running = manager.ExecuteAsync(Credentials(), blocked.RunAsync, Token);
        await blocked.Started;
        var queued = manager.ExecuteAsync(Credentials(), SlowCommandAsync, Token);
        var releasing = manager.ReleaseAsync("s1", Token);

        blocked.Release();
        await running;
        Assert.Equal(1, await queued);
        await releasing;
        Assert.True(factory.Sessions.Single().Disposed);

        await manager.ExecuteAsync(Credentials(), SlowCommandAsync, Token);
        Assert.Equal(2, factory.Opened);
    }

    [Fact]
    public async Task Releasing_An_Unknown_Session_Does_Nothing()
    {
        await using var manager = NewManager(new FakeFactory());

        await manager.ReleaseAsync("nope", Token);

        Assert.Equal(0, manager.ConnectionCount);
    }

    [Fact]
    public async Task Disposing_The_Manager_Closes_Every_Connection()
    {
        var factory = new FakeFactory();
        var manager = NewManager(factory);
        await manager.ExecuteAsync(Credentials("s1"), SlowCommandAsync, Token);
        await manager.ExecuteAsync(Credentials("s2"), SlowCommandAsync, Token);

        await manager.DisposeAsync();

        Assert.All(factory.Sessions, s => Assert.True(s.Disposed));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.ExecuteAsync(Credentials(), SlowCommandAsync, Token));
    }
}
