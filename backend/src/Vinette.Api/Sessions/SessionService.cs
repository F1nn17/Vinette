using Microsoft.Extensions.Options;
using Vinette.Api.Configuration;
using Vinette.Core.Sessions;

namespace Vinette.Api.Sessions;

/// <summary>
/// Session life cycle: create (password encrypted), validate and extend (idle and absolute limits), end.
/// A session ends after <c>Session:IdleTimeout</c> without activity or <c>Session:MaxLifetime</c> after login;
/// "stay signed in" sessions use <c>Session:RememberMeLifetime</c> for both.
/// </summary>
public sealed class SessionService(
    ISessionStore store,
    ICredentialProtector protector,
    IOptions<Configuration.SessionLifetimeOptions> options,
    TimeProvider time)
{
    public async Task<Session> CreateAsync(
        string address,
        string password,
        bool rememberMe,
        string? clientIp,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var session = new Session(
            SessionId.Create(),
            address,
            protector.Protect(password),
            CreatedAt: now,
            LastSeenAt: now,
            rememberMe,
            clientIp);

        await store.AddAsync(session, cancellationToken);
        return session;
    }

    /// <summary>
    /// Returns the live session for a cookie value and records the activity; null when the id is malformed,
    /// unknown or the session has expired (an expired session is removed on the spot).
    /// </summary>
    public async Task<Session?> GetValidAsync(string? sessionId, CancellationToken cancellationToken)
    {
        if (!SessionId.IsWellFormed(sessionId))
        {
            return null;
        }

        var session = await store.GetAsync(sessionId!, cancellationToken);
        if (session is null)
        {
            return null;
        }

        var now = time.GetUtcNow();
        if (IsExpired(session, now))
        {
            await store.RemoveAsync(session.Id, cancellationToken);
            return null;
        }

        await store.TouchAsync(session.Id, now, cancellationToken);
        return session with { LastSeenAt = now };
    }

    /// <summary>The mailbox password in clear text, for opening a mail server connection. Never log or return it.</summary>
    public string RevealPassword(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return protector.Unprotect(session.ProtectedPassword);
    }

    public Task EndAsync(string sessionId, CancellationToken cancellationToken) =>
        store.RemoveAsync(sessionId, cancellationToken);

    public Task<int> EndAllAsync(string address, CancellationToken cancellationToken) =>
        store.RemoveAllForAddressAsync(address, cancellationToken);

    /// <summary>Drops every expired session; run periodically so abandoned sessions do not pile up.</summary>
    public Task<int> RemoveExpiredAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        return store.RemoveWhereAsync(session => IsExpired(session, now), cancellationToken);
    }

    internal bool IsExpired(Session session, DateTimeOffset now)
    {
        var settings = options.Value;
        var idleLimit = session.RememberMe ? settings.RememberMeLifetime : settings.IdleTimeout;
        var absoluteLimit = session.RememberMe ? settings.RememberMeLifetime : settings.MaxLifetime;

        return now - session.LastSeenAt > idleLimit || now - session.CreatedAt > absoluteLimit;
    }
}
