using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Vinette.Api.Auth;
using Vinette.Api.Sessions;
using Vinette.Core.Mail;

namespace Vinette.Api.Hubs;

/// <summary>
/// Real-time mailbox events (<c>/hubs/mail</c>). Needs a session like every endpoint. The client calls nothing: it connects and
/// is sent the client method <c>mailEvent</c> with a <see cref="MailEventDto"/> whenever something happens in its inbox.
/// </summary>
public sealed class MailHub(MailHubConnections connections) : Hub
{
    public const string EventMethod = "mailEvent";

    public override Task OnConnectedAsync()
    {
        var session = Context.GetHttpContext()?.GetSession();
        if (session is null)
        {
            Context.Abort(); // not reachable: the hub requires a session
            return Task.CompletedTask;
        }

        connections.Open(Context, session.Id, session.Address);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Close(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}

/// <summary>
/// The open connections of the hub: each listens to its mailbox through <see cref="IMailboxWatcher"/> (which keeps one watching
/// connection per mailbox, however many browsers listen) and is sent the events; a connection whose web session has ended
/// (sign-out, expiry, a password that stopped working) is closed, so that nobody keeps receiving a mailbox after signing out.
/// The password is not kept: it is revealed from the session whenever the watching connection has to be opened.
/// </summary>
public sealed partial class MailHubConnections(
    IMailboxWatcher watcher,
    IHubContext<MailHub> hub,
    SessionService sessions,
    ILogger<MailHubConnections> logger,
    TimeSpan? sessionCheckInterval = null)
{
    private readonly TimeSpan checkEvery = sessionCheckInterval ?? TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, Entry> open = new(StringComparer.Ordinal);

    /// <summary>Connections that are listening now.</summary>
    public int Count => open.Count;

    public void Open(HubCallerContext context, string sessionId, string address)
    {
        var connectionId = context.ConnectionId;
        var stop = new CancellationTokenSource();
        var subscription = watcher.Subscribe(
            address,
            sessionId,
            async cancellationToken =>
            {
                var session = await sessions.PeekAsync(sessionId, cancellationToken).ConfigureAwait(false);
                return session is null ? null : new MailCredentials(session.Id, session.Address, sessions.RevealPassword(session), session.ClientIp);
            },
            change => new ValueTask(hub.Clients.Client(connectionId).SendAsync(MailHub.EventMethod, MailEventMapping.ToDto(change), stop.Token)));
        var entry = new Entry(subscription, stop);
        open[connectionId] = entry;
        _ = Task.Run(() => KeepWhileSessionLivesAsync(context, sessionId, stop.Token));
    }

    public void Close(string connectionId)
    {
        if (open.TryRemove(connectionId, out var entry))
        {
            entry.Subscription.Dispose();
            entry.Stop.Cancel();
            entry.Stop.Dispose();
        }
    }

    private async Task KeepWhileSessionLivesAsync(HubCallerContext context, string sessionId, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(checkEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await sessions.PeekAsync(sessionId, cancellationToken).ConfigureAwait(false) is null)
                {
                    context.Abort();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // the connection closed by itself
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SessionCheckFailed(logger, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Checking the session of a real-time connection failed.")]
    private static partial void SessionCheckFailed(ILogger logger, Exception exception);

    private sealed record Entry(IDisposable Subscription, CancellationTokenSource Stop);
}
