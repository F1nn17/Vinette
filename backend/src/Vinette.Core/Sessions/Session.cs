namespace Vinette.Core.Sessions;

/// <summary>
/// A signed-in user. The browser only ever sees <see cref="Id"/> (httpOnly cookie); the password is kept on the
/// server and only in protected form (<see cref="ICredentialProtector"/>).
/// </summary>
/// <param name="Id">Opaque identifier, see <see cref="SessionId"/>.</param>
/// <param name="Address">The mailbox address the user logged in with.</param>
/// <param name="ProtectedPassword">Encrypted password; decrypted only to open a mail server connection.</param>
/// <param name="CreatedAt">Absolute lifetime (Session:MaxLifetime) is counted from here.</param>
/// <param name="LastSeenAt">Idle timeout (Session:IdleTimeout) is counted from here.</param>
/// <param name="RememberMe">The user chose to stay signed in.</param>
/// <param name="ClientIp">Client address at login, for audit.</param>
public sealed record Session(
    string Id,
    string Address,
    ReadOnlyMemory<byte> ProtectedPassword,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    bool RememberMe,
    string? ClientIp)
{
    /// <summary>Redacted: the session id is a bearer secret and the password blob must not reach logs.</summary>
    public override string ToString() =>
        $"Session {{ Address = {Address}, CreatedAt = {CreatedAt:O}, Id = ***, ProtectedPassword = *** }}";
}
