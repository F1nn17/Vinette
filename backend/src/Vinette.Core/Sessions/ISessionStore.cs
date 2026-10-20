namespace Vinette.Core.Sessions;

/// <summary>
/// Storage for sessions (in memory by default, Redis optionally). It only stores: idle and absolute timeouts
/// are enforced by the session service from <see cref="Session.LastSeenAt"/> and <see cref="Session.CreatedAt"/>.
/// </summary>
public interface ISessionStore
{
    Task AddAsync(Session session, CancellationToken cancellationToken);

    Task<Session?> GetAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Updates <see cref="Session.LastSeenAt"/>. Returns false when the session does not exist.</summary>
    Task<bool> TouchAsync(string sessionId, DateTimeOffset seenAt, CancellationToken cancellationToken);

    /// <summary>Returns false when the session did not exist.</summary>
    Task<bool> RemoveAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Signs a mailbox out everywhere (password changed, access revoked). Returns the number of removed sessions.</summary>
    Task<int> RemoveAllForAddressAsync(string address, CancellationToken cancellationToken);
}
