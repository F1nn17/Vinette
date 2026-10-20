namespace Vinette.Core.Authentication;

/// <summary>Checks credentials against the mail server (implemented over IMAP LOGIN in Vinette.Mail).</summary>
public interface IAuthenticator
{
    /// <summary>
    /// Returns an outcome for expected failures (wrong password, server down); throws only for unexpected errors
    /// and when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    Task<AuthenticationResult> AuthenticateAsync(
        LoginRequest request,
        ClientInfo client,
        CancellationToken cancellationToken);
}
