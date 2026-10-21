using System.Collections.Concurrent;
using Vinette.Core.Mail;

namespace Vinette.Mail;

/// <summary>
/// <see cref="IMailGateway"/> that keeps one mail connection per web session: opened by the first command, reused by
/// the next ones, closed when the session ends. Commands of one session run one after another (the connection is never
/// used concurrently); different sessions do not wait for each other.
/// </summary>
public sealed class MailConnectionManager(IMailSessionFactory factory) : IMailGateway, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Connection> connections = new(StringComparer.Ordinal);
    private bool disposed;

    /// <summary>Number of web sessions that currently have a connection slot.</summary>
    public int ConnectionCount => connections.Count;

    public async Task<TResult> ExecuteAsync<TResult>(
        MailCredentials credentials,
        Func<IMailSession, CancellationToken, Task<TResult>> command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(command);
        ObjectDisposedException.ThrowIf(disposed, this);

        var connection = connections.GetOrAdd(credentials.SessionId, static _ => new Connection());
        await connection.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (connection.Closed)
            {
                // The web session ended while this command was waiting: do not open a connection nobody will close.
                throw new MailAccessRevokedException("The session has ended.");
            }

            if (connection.Session is not { IsConnected: true } session)
            {
                if (connection.Session is not null)
                {
                    await connection.Session.DisposeAsync().ConfigureAwait(false);
                    connection.Session = null;
                }

                session = await factory.OpenAsync(credentials, cancellationToken).ConfigureAwait(false);
                connection.Session = session;
            }

            return await command(session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = connection.Gate.Release();
        }
    }

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

        public bool Closed { get; set; }
    }
}
