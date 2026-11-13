using Vinette.Core.Mail;

namespace Vinette.Mail;

/// <summary>How the watching connections behave.</summary>
/// <param name="IdleRestart">An IDLE is ended and started again this often: servers drop one that lasts too long (RFC 2177 says 29 minutes).</param>
/// <param name="Grace">A watch outlives its last listener by this long, so that a page reload does not close and open it again.</param>
/// <param name="MaxConnections">Watching connections of the whole application; a watch that finds none free waits and tries again.</param>
/// <param name="MaxBackoff">The longest wait before a watch tries again after a failure.</param>
public sealed record MailboxWatchSettings(TimeSpan IdleRestart, TimeSpan Grace, int MaxConnections, TimeSpan MaxBackoff)
{
    public static MailboxWatchSettings Default { get; } = new(TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(1), 200, TimeSpan.FromMinutes(1));
}

/// <summary>
/// <see cref="IMailboxWatcher"/> over IMAP IDLE. One connection per mailbox, shared by every listener of it (every web session of
/// that mailbox), opened with the first listener and closed <see cref="MailboxWatchSettings.Grace"/> after the last. The connection
/// is opened with the credentials of any listener that still has a live session, asked for each time, so no password is kept here.
/// A lost connection is opened again after a growing pause; a rejected password drops the listener that gave it.
/// </summary>
public sealed class MailboxWatcher(IMailSessionFactory factory, MailboxWatchSettings? watchSettings = null, TimeProvider? timeProvider = null) : IMailboxWatcher, IAsyncDisposable
{
    private readonly MailboxWatchSettings settings = watchSettings ?? MailboxWatchSettings.Default;
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim slots = new((watchSettings ?? MailboxWatchSettings.Default).MaxConnections);
    private readonly Lock gate = new();
    private readonly Dictionary<string, Watch> watches = new(StringComparer.OrdinalIgnoreCase);
    private int openCount;
    private bool disposed;

    /// <summary>Mailboxes that are being watched (a connection is open, or being opened).</summary>
    public int WatchCount
    {
        get
        {
            lock (gate)
            {
                return watches.Count;
            }
        }
    }

    /// <summary>Watching connections that are open now.</summary>
    public int ConnectionCount => Volatile.Read(ref openCount);

    public IDisposable Subscribe(
        string address,
        string sessionId,
        Func<CancellationToken, ValueTask<MailCredentials?>> credentials,
        Func<MailboxEvent, ValueTask> onChange)
    {
        ArgumentException.ThrowIfNullOrEmpty(address);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(onChange);
        var listener = new Listener(sessionId, credentials, onChange);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var created = !watches.TryGetValue(address, out var watch);
            if (created)
            {
                watch = new Watch(address);
                watches[address] = watch;
            }

            watch!.Listeners.Add(listener); // before the loop starts: a loop that finds nobody listening ends
            watch.CancelGrace();
            if (created)
            {
                var started = watch;
                started.Loop = Task.Run(() => RunAsync(started));
            }

            return new Subscription(() => Unsubscribe(watch, listener));
        }
    }

    private void Unsubscribe(Watch watch, Listener listener)
    {
        lock (gate)
        {
            _ = watch.Listeners.Remove(listener);
            if (watch.Listeners.Count == 0 && !disposed)
            {
                StartGrace(watch);
            }
        }
    }

    // Called with the lock held.
    private void StartGrace(Watch watch)
    {
        watch.CancelGrace();
        var grace = new CancellationTokenSource();
        watch.GraceSource = grace;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(settings.Grace, time, grace.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // somebody listens again
            }

            lock (gate)
            {
                if (watch.Listeners.Count == 0)
                {
                    Retire(watch);
                }
            }
        });
    }

    // Called with the lock held: nobody listens, the connection is closed.
    private void Retire(Watch watch)
    {
        if (watches.TryGetValue(watch.Address, out var current) && ReferenceEquals(current, watch))
        {
            _ = watches.Remove(watch.Address);
        }

        watch.Stop.Cancel();
    }

    private async Task RunAsync(Watch watch)
    {
        var token = watch.Stop.Token;
        var failures = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var chosen = await NextCredentialsAsync(watch, token).ConfigureAwait(false);
                if (chosen is null)
                {
                    lock (gate)
                    {
                        if (watch.Listeners.Count == 0)
                        {
                            Retire(watch);
                            return; // every listener's session has ended
                        }
                    }

                    continue;
                }

                if (!await slots.WaitAsync(0, token).ConfigureAwait(false))
                {
                    failures++; // all the places are taken: wait and look again
                    await Task.Delay(Backoff(failures), time, token).ConfigureAwait(false);
                    continue;
                }

                var started = time.GetTimestamp();
                try
                {
                    await using var session = await factory.OpenAsync(chosen.Value.Credentials, token).ConfigureAwait(false);
                    _ = Interlocked.Increment(ref openCount);
                    try
                    {
                        await session.WatchInboxAsync(change => DispatchAsync(watch, change), settings.IdleRestart, watch.State, token).ConfigureAwait(false);
                    }
                    finally
                    {
                        _ = Interlocked.Decrement(ref openCount);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (MailAccessRevokedException)
                {
                    lock (gate)
                    {
                        _ = watch.Listeners.Remove(chosen.Value.Listener); // its session's password is no good any more
                    }

                    continue;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // Lost or refused: try again after a pause that grows with the failures in a row.
                    failures = time.GetElapsedTime(started) > TimeSpan.FromSeconds(30) ? 1 : failures + 1;
                    await Task.Delay(Backoff(failures), time, token).ConfigureAwait(false);
                    continue;
                }
                finally
                {
                    _ = slots.Release();
                }

                failures = 0;
            }
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
    }

    // The credentials of the first listener whose web session is still alive; listeners whose session has ended are dropped.
    private async Task<(MailCredentials Credentials, Listener Listener)?> NextCredentialsAsync(Watch watch, CancellationToken cancellationToken)
    {
        Listener[] snapshot;
        lock (gate)
        {
            snapshot = [.. watch.Listeners];
        }

        foreach (var listener in snapshot)
        {
            var credentials = await listener.Credentials(cancellationToken).ConfigureAwait(false);
            if (credentials is not null)
            {
                return (credentials, listener);
            }

            lock (gate)
            {
                _ = watch.Listeners.Remove(listener);
            }
        }

        return null;
    }

    private async ValueTask DispatchAsync(Watch watch, MailboxEvent change)
    {
        Listener[] snapshot;
        lock (gate)
        {
            snapshot = [.. watch.Listeners];
        }

        foreach (var listener in snapshot)
        {
            try
            {
                await listener.OnChange(change).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A listener that fails must not stop the others, nor the watch.
            }
        }
    }

    // One second, doubling, at most MaxBackoff, spread by up to 20 percent so that many mailboxes do not come back together.
    private TimeSpan Backoff(int failures)
    {
        var seconds = Math.Min(settings.MaxBackoff.TotalSeconds, Math.Pow(2, Math.Min(failures, 20) - 1));
        return TimeSpan.FromSeconds(seconds * (1 + (((Random.Shared.NextDouble() * 2) - 1) * 0.2)));
    }

    public async ValueTask DisposeAsync()
    {
        Task[] loops;
        lock (gate)
        {
            disposed = true;
            loops = [.. watches.Values.Select(w => w.Loop).OfType<Task>()];
            foreach (var watch in watches.Values)
            {
                watch.CancelGrace();
                watch.Stop.Cancel();
            }

            watches.Clear();
        }

        await Task.WhenAll(loops).ConfigureAwait(false);
        slots.Dispose();
    }

    private sealed record Listener(string SessionId, Func<CancellationToken, ValueTask<MailCredentials?>> Credentials, Func<MailboxEvent, ValueTask> OnChange);

    private sealed class Watch(string address)
    {
        public string Address { get; } = address;

        public List<Listener> Listeners { get; } = [];

        /// <summary>What the watch knows of the inbox, from one connection to the next.</summary>
        public MailboxWatchState State { get; } = new();

        public CancellationTokenSource Stop { get; } = new();

        public CancellationTokenSource? GraceSource { get; set; }

        public Task? Loop { get; set; }

        public void CancelGrace()
        {
            GraceSource?.Cancel();
            GraceSource = null;
        }
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private int done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref done, 1) == 0)
            {
                unsubscribe();
            }
        }
    }
}
