using System.Collections.Concurrent;
using Vinette.Core.Mail;

namespace Vinette.Mail;

/// <summary>
/// <see cref="IMailGateway"/> that keeps one mail connection per web session: opened by the first command, reused by
/// the next ones, closed when the session ends. Commands of one session run one after another (the connection is never
/// used concurrently); different sessions do not wait for each other.
/// </summary>
public sealed class MailConnectionManager(
    IMailSessionFactory factory,
    MailReconnectPolicy? reconnectPolicy = null,
    TimeProvider? timeProvider = null) : IMailGateway, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Connection> connections = new(StringComparer.Ordinal);
    private readonly MailReconnectPolicy policy = reconnectPolicy ?? MailReconnectPolicy.Default;
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private bool disposed;

    /// <summary>Number of web sessions that currently have a connection slot.</summary>
    public int ConnectionCount => connections.Count;

    public async Task<TResult> ExecuteAsync<TResult>(
        MailCredentials credentials,
        Func<IMailSession, CancellationToken, Task<TResult>> command,
        CancellationToken cancellationToken,
        bool safeToRepeat = true)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(command);
        ObjectDisposedException.ThrowIf(disposed, this);

        while (true)
        {
            var connection = connections.GetOrAdd(credentials.SessionId, _ => new Connection { LastUsed = time.GetTimestamp() });
            await connection.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (connection.Evicted)
                {
                    continue; // closed for idleness while this command waited: take the current slot of the session
                }

                if (connection.Closed)
                {
                    // The web session ended while this command was waiting: do not open a connection nobody will close.
                    throw new MailAccessRevokedException("The session has ended.");
                }

                return await RunWithReconnectAsync(connection, credentials, command, safeToRepeat, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                connection.LastUsed = time.GetTimestamp();
                _ = connection.Gate.Release();
            }
        }
    }

    /// <summary>
    /// Closes the connections that have not been used for <paramref name="idleFor"/> (a command that is running or
    /// waiting keeps its connection). The next command of such a session simply opens a new one. Returns how many were closed.
    /// </summary>
    public async Task<int> CloseIdleAsync(TimeSpan idleFor, CancellationToken cancellationToken)
    {
        var closed = 0;
        foreach (var (id, connection) in connections)
        {
            if (time.GetElapsedTime(connection.LastUsed) < idleFor || !await connection.Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                continue; // in use, or used recently
            }

            try
            {
                if (time.GetElapsedTime(connection.LastUsed) < idleFor || connection.Closed || connection.Evicted)
                {
                    continue;
                }

                connection.Evicted = true;
                _ = connections.TryRemove(new KeyValuePair<string, Connection>(id, connection));
                if (connection.Session is not null)
                {
                    await DropAsync(connection).ConfigureAwait(false);
                    closed++;
                }
            }
            finally
            {
                _ = connection.Gate.Release();
            }
        }

        return closed;
    }

    // Connection-level failures (cannot connect, connection lost) are retried with growing pauses; a rejected password never is.
    private async Task<TResult> RunWithReconnectAsync<TResult>(
        Connection connection,
        MailCredentials credentials,
        Func<IMailSession, CancellationToken, Task<TResult>> command,
        bool safeToRepeat,
        CancellationToken cancellationToken)
    {
        var failures = 0;
        var giveUp = false;
        while (true)
        {
            Exception failure;
            try
            {
                var session = await EnsureSessionAsync(connection, credentials, cancellationToken).ConfigureAwait(false);
                try
                {
                    return await command(session, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // A command cut off in the middle leaves the protocol stream in an unknown state (the server may still be
                    // answering it): the connection is dropped and the next command opens a clean one.
                    await DropAsync(connection).ConfigureAwait(false);
                    throw;
                }
                catch (Exception exception) when (IsConnectionFailure(exception))
                {
                    await DropAsync(connection).ConfigureAwait(false);
                    if (!safeToRepeat)
                    {
                        giveUp = true;
                        throw AsUnavailable(exception); // the command may have been carried out: never run it twice
                    }

                    failure = exception;
                }
            }
            catch (MailUnavailableException exception) when (!giveUp)
            {
                failure = exception; // could not connect
            }

            failures++;
            if (failures > policy.MaxRetries)
            {
                throw AsUnavailable(failure);
            }

            await Task.Delay(policy.DelayBefore(failures), time, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<IMailSession> EnsureSessionAsync(Connection connection, MailCredentials credentials, CancellationToken cancellationToken)
    {
        if (connection.Session is { IsConnected: true } existing)
        {
            return existing;
        }

        await DropAsync(connection).ConfigureAwait(false);
        var session = await factory.OpenAsync(credentials, cancellationToken).ConfigureAwait(false);
        connection.Session = session;
        return session;
    }

    private static async Task DropAsync(Connection connection)
    {
        if (connection.Session is { } session)
        {
            connection.Session = null;
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool IsConnectionFailure(Exception exception) =>
        exception is MailUnavailableException or IOException or System.Net.Sockets.SocketException or TimeoutException
            or MailKit.ServiceNotConnectedException or MailKit.ProtocolException;

    private static MailUnavailableException AsUnavailable(Exception exception) =>
        exception as MailUnavailableException ?? new MailUnavailableException($"{exception.GetType().Name}: {exception.Message}", exception);

    public async Task ReleaseAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        if (connections.TryRemove(sessionId, out var connection))
        {
            await CloseAsync(connection, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        foreach (var id in connections.Keys)
        {
            if (connections.TryRemove(id, out var connection))
            {
                await CloseAsync(connection, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    // Waits for the command that is running (if any), then closes the connection.
    private static async Task CloseAsync(Connection connection, CancellationToken cancellationToken)
    {
        await connection.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            connection.Closed = true;
            if (connection.Session is not null)
            {
                await connection.Session.DisposeAsync().ConfigureAwait(false);
                connection.Session = null;
            }
        }
        finally
        {
            _ = connection.Gate.Release();
        }
    }

    private sealed class Connection
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public IMailSession? Session { get; set; }

        /// <summary>The web session ended: the slot must not be used again.</summary>
        public bool Closed { get; set; }

        /// <summary>Taken out of the table because it was idle: a waiting command must look the session up again.</summary>
        public bool Evicted { get; set; }

        public long LastUsed { get; set; }
    }
}
