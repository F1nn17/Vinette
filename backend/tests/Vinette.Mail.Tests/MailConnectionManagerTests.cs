using Vinette.Core.Mail;

namespace Vinette.Mail.Tests;

/// <summary>The connection manager against a fake mail server: no network, no Docker.</summary>
public sealed class MailConnectionManagerTests
{
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

        public int Opened => Volatile.Read(ref opened);

        public async Task<IMailSession> OpenAsync(MailCredentials credentials, CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref opened);
            if (OpenDelay > TimeSpan.Zero)
            {
                await Task.Delay(OpenDelay, cancellationToken);
            }

            if (FailWith is not null)
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
        await using var manager = new MailConnectionManager(factory);

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => manager.ExecuteAsync(Credentials(), SlowCommandAsync, Token)));

        Assert.Equal(1, factory.Opened);
        Assert.Equal(1, factory.Sessions.Single().MaxRunning);
        Assert.Equal(1, manager.ConnectionCount);
    }

    [Fact]
    public async Task Different_Sessions_Get_Their_Own_Connections()
    {
        var factory = new FakeFactory();
        await using var manager = new MailConnectionManager(factory);

        await manager.ExecuteAsync(Credentials("s1"), SlowCommandAsync, Token);
        await manager.ExecuteAsync(Credentials("s2", "bob@vinette.test"), SlowCommandAsync, Token);

        Assert.Equal(2, factory.Opened);
        Assert.Equal(2, manager.ConnectionCount);
    }

    [Fact]
    public async Task Different_Sessions_Do_Not_Wait_For_Each_Other()
    {
        await using var manager = new MailConnectionManager(new FakeFactory());
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
        await using var manager = new MailConnectionManager(factory);
        await manager.ExecuteAsync(Credentials(), SlowCommandAsync, Token);
        factory.Sessions[0].IsConnected = false;

        await manager.ExecuteAsync(Credentials(), SlowCommandAsync, Token);

        Assert.Equal(2, factory.Opened);
        Assert.True(factory.Sessions[0].Disposed);
    }

    [Fact]
    public async Task A_Failed_Open_Is_Not_Remembered()
    {
        var factory = new FakeFactory { FailWith = new MailUnavailableException("down") };
        await using var manager = new MailConnectionManager(factory);
        await Assert.ThrowsAsync<MailUnavailableException>(() => manager.ExecuteAsync(Credentials(), SlowCommandAsync, Token));

        factory.FailWith = null;
        var result = await manager.ExecuteAsync(Credentials(), SlowCommandAsync, Token);

        Assert.Equal(1, result);
        Assert.Equal(2, factory.Opened);
    }

    [Fact]
    public async Task A_Failing_Command_Does_Not_Block_The_Next_One()
    {
        var factory = new FakeFactory();
        await using var manager = new MailConnectionManager(factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.ExecuteAsync<int>(Credentials(), (_, _) => throw new InvalidOperationException("boom"), Token));
        var result = await manager.ExecuteAsync(Credentials(), SlowCommandAsync, Token);

        Assert.Equal(1, result);
        Assert.Equal(1, factory.Opened);
    }

    [Fact]
    public async Task A_Cancelled_Waiter_Leaves_The_Queue_Without_Breaking_It()
    {
        await using var manager = new MailConnectionManager(new FakeFactory());
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
        await using var manager = new MailConnectionManager(factory);
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
        await using var manager = new MailConnectionManager(new FakeFactory());
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
        await using var manager = new MailConnectionManager(factory);
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
        await using var manager = new MailConnectionManager(factory);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var opening = manager.ExecuteAsync(Credentials(), SlowCommandAsync, cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);

        factory.OpenDelay = TimeSpan.Zero;

        Assert.Equal(1, await manager.ExecuteAsync(Credentials(), SlowCommandAsync, Token));
    }

    [Fact]
    public async Task Releasing_A_Session_Closes_Its_Connection()
    {
        var factory = new FakeFactory();
        await using var manager = new MailConnectionManager(factory);
        await manager.ExecuteAsync(Credentials(), SlowCommandAsync, Token);

        await manager.ReleaseAsync("s1", Token);

        Assert.True(factory.Sessions.Single().Disposed);
        Assert.Equal(0, manager.ConnectionCount);
    }

    [Fact]
    public async Task Releasing_Waits_For_The_Running_Command()
    {
        var factory = new FakeFactory();
        await using var manager = new MailConnectionManager(factory);
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
        await using var manager = new MailConnectionManager(factory);
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
        await using var manager = new MailConnectionManager(new FakeFactory());

        await manager.ReleaseAsync("nope", Token);

        Assert.Equal(0, manager.ConnectionCount);
    }

    [Fact]
    public async Task Disposing_The_Manager_Closes_Every_Connection()
    {
        var factory = new FakeFactory();
        var manager = new MailConnectionManager(factory);
        await manager.ExecuteAsync(Credentials("s1"), SlowCommandAsync, Token);
        await manager.ExecuteAsync(Credentials("s2"), SlowCommandAsync, Token);

        await manager.DisposeAsync();

        Assert.All(factory.Sessions, s => Assert.True(s.Disposed));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.ExecuteAsync(Credentials(), SlowCommandAsync, Token));
    }
}
