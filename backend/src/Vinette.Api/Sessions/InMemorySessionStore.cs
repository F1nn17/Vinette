using System.Collections.Concurrent;
using Vinette.Core.Sessions;

namespace Vinette.Api.Sessions;

/// <summary>Default session store: process memory. Sessions do not survive a restart and are not shared between instances.</summary>
public sealed class InMemorySessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<string, Session> sessions = new(StringComparer.Ordinal);

    public Task AddAsync(Session session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!sessions.TryAdd(session.Id, session))
        {
            throw new InvalidOperationException("A session with this id already exists.");
        }

        return Task.CompletedTask;
    }

    public Task<Session?> GetAsync(string sessionId, CancellationToken cancellationToken) =>
        Task.FromResult(sessions.GetValueOrDefault(sessionId));

    public Task<bool> TouchAsync(string sessionId, DateTimeOffset seenAt, CancellationToken cancellationToken)
    {
        while (sessions.TryGetValue(sessionId, out var current))
        {
            if (seenAt <= current.LastSeenAt || sessions.TryUpdate(sessionId, current with { LastSeenAt = seenAt }, current))
            {
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }

    public Task<bool> RemoveAsync(string sessionId, CancellationToken cancellationToken) =>
        Task.FromResult(sessions.TryRemove(sessionId, out _));

    public Task<int> RemoveWhereAsync(Func<Session, bool> predicate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var removed = 0;
        foreach (var session in sessions.Values.Where(predicate))
        {
            if (sessions.TryRemove(session.Id, out _))
            {
                removed++;
            }
        }

        return Task.FromResult(removed);
    }

    public Task<int> RemoveAllForAddressAsync(string address, CancellationToken cancellationToken) =>
        RemoveWhereAsync(s => string.Equals(s.Address, address, StringComparison.OrdinalIgnoreCase), cancellationToken);
}
