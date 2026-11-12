using System.Diagnostics.Metrics;
using Vinette.Core.Mail;

namespace Vinette.Mail.Tests;

/// <summary>The metrics one connection manager emits (VIN-035), read through a listener bound to that manager's own meter.</summary>
public sealed class MailMetricsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class FakeSession : IMailSession
    {
        public bool IsConnected { get; private set; } = true;

        public Task PingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<MailFolderInfo>> GetFoldersAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MailFolderInfo>>([]);


        public Task<MailQuota?> GetQuotaAsync(CancellationToken cancellationToken) => Task.FromResult<MailQuota?>(null);

        public Task<MessageSource> GetMessageAsync(MessageId id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MessageFlagState> UpdateFlagsAsync(MessageId id, MessageFlagChange change, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MessageId?> MoveMessageAsync(MessageId id, string targetFolder, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteMessagePermanentlyAsync(MessageId id, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> GetLabelsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> UpdateMessageLabelsAsync(MessageId id, IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> RenameLabelAsync(string oldName, string? newName, CancellationToken cancellationToken) => throw new NotSupportedException();



        public Task<MessagePage> ListMessagesAsync(MessageListQuery query, CancellationToken cancellationToken) => Task.FromResult(new MessagePage(1, [], null, 0));

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeFactory : IMailSessionFactory
    {
        public Exception? FailWith { get; set; }

        public Task<IMailSession> OpenAsync(MailCredentials credentials, CancellationToken cancellationToken) =>
            FailWith is not null ? throw FailWith : Task.FromResult<IMailSession>(new FakeSession());
    }

    private sealed class Recorder : IDisposable
    {
        private readonly MeterListener listener = new();

        public Recorder(Meter meter)
        {
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter == meter)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((i, value, tags, _) => Add(i.Name, value, tags));
            listener.SetMeasurementEventCallback<int>((i, value, tags, _) => Add(i.Name, value, tags));
            listener.SetMeasurementEventCallback<double>((i, value, tags, _) => Add(i.Name, 1, tags));
            listener.Start();
        }

        public Dictionary<string, long> Totals { get; } = [];

        public void Observe() => listener.RecordObservableInstruments();

        public long Get(string name, string? tag = null) => Totals.GetValueOrDefault(tag is null ? name : $"{name}|{tag}");

        private void Add(string name, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            Totals[name] = Totals.GetValueOrDefault(name) + value;
            foreach (var tag in tags)
            {
                var key = $"{name}|{tag.Value}";
                Totals[key] = Totals.GetValueOrDefault(key) + value;
            }
        }

        public void Dispose() => listener.Dispose();
    }

    private static Task<int> Use(MailConnectionManager manager, string session = "s1") =>
        manager.ExecuteAsync(new MailCredentials(session, "alice@vinette.test", "pw"), (_, _) => Task.FromResult(1), Token);

    [Fact]
    public async Task Opening_Using_And_Releasing_A_Connection_Is_Counted()
    {
        await using var manager = new MailConnectionManager(new FakeFactory());
        using var recorder = new Recorder(manager.Metrics.Meter);

        await Use(manager);
        await Use(manager);
        recorder.Observe();
        Assert.Equal(1, recorder.Get("vinette.mail.connections.open")); // the gauge reads the current value
        await manager.ReleaseAsync("s1", Token);

        Assert.Equal(1, recorder.Get("vinette.mail.connections.opened"));
        Assert.Equal(1, recorder.Get("vinette.mail.connections.closed", "released"));
        Assert.Equal(2, recorder.Get("vinette.mail.command.duration", "ok"));
    }

    [Fact]
    public async Task Idle_Closing_And_Shutdown_Are_Told_Apart()
    {
        var clock = new ManualClock();
        var manager = new MailConnectionManager(new FakeFactory(), null, clock);
        using var recorder = new Recorder(manager.Metrics.Meter);
        await Use(manager, "a");
        clock.Advance(TimeSpan.FromMinutes(10));
        _ = await manager.CloseIdleAsync(TimeSpan.FromMinutes(5), Token);
        await Use(manager, "b");

        await manager.DisposeAsync();

        Assert.Equal(1, recorder.Get("vinette.mail.connections.closed", "idle"));
        Assert.Equal(1, recorder.Get("vinette.mail.connections.closed", "shutdown"));
    }

    [Fact]
    public async Task Failures_Are_Counted_By_Kind_And_Never_By_User()
    {
        var factory = new FakeFactory { FailWith = new MailUnavailableException("down") };
        await using var manager = new MailConnectionManager(factory, new MailReconnectPolicy(2, TimeSpan.Zero, 1, 0));
        using var recorder = new Recorder(manager.Metrics.Meter);

        await Assert.ThrowsAsync<MailUnavailableException>(() => Use(manager));
        factory.FailWith = new MailAccessRevokedException("changed");
        await Assert.ThrowsAsync<MailAccessRevokedException>(() => Use(manager, "s2"));

        Assert.Equal(1, recorder.Get("vinette.mail.errors", "unavailable"));
        Assert.Equal(1, recorder.Get("vinette.mail.errors", "revoked"));
        Assert.Equal(2, recorder.Get("vinette.mail.reconnects"));
        Assert.DoesNotContain(recorder.Totals.Keys, k => k.Contains('@', StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_Busy_Refusal_Is_Counted()
    {
        var limits = new MailConnectionLimits(1, 10, TimeSpan.FromMilliseconds(50));
        await using var manager = new MailConnectionManager(new FakeFactory(), null, null, limits);
        using var recorder = new Recorder(manager.Metrics.Meter);
        await Use(manager, "one");

        await Assert.ThrowsAsync<MailBusyException>(() => Use(manager, "two"));

        Assert.Equal(1, recorder.Get("vinette.mail.errors", "busy"));
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
    }
}
