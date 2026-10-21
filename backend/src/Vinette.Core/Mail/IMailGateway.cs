namespace Vinette.Core.Mail;

/// <summary>
/// The only way for the rest of the application to talk to the mail server on behalf of a user. The gateway owns the
/// connections: it reuses one per web session, runs commands of one session strictly one after another and hides
/// reconnects. Endpoints and services depend on this interface only, never on the protocol library.
/// </summary>
public interface IMailGateway
{
    /// <summary>
    /// Runs <paramref name="command"/> on the user's mail session once earlier commands of the same web session are done.
    /// Throws <see cref="MailUnavailableException"/> or <see cref="MailAccessRevokedException"/> when the mailbox cannot be reached.
    /// </summary>
    Task<TResult> ExecuteAsync<TResult>(
        MailCredentials credentials,
        Func<IMailSession, CancellationToken, Task<TResult>> command,
        CancellationToken cancellationToken);

    /// <summary>Closes the mail connection of a web session that has ended (sign-out, expiry). Does nothing if there is none.</summary>
    Task ReleaseAsync(string sessionId, CancellationToken cancellationToken);
}
