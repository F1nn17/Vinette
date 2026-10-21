namespace Vinette.Core.Mail;

/// <summary>Opens connections to the mail server (implemented over IMAP in Vinette.Mail).</summary>
public interface IMailSessionFactory
{
    /// <summary>
    /// Connects and signs in. Throws <see cref="MailAccessRevokedException"/> when the server rejects the credentials
    /// and <see cref="MailUnavailableException"/> when it cannot be reached; the caller owns the returned session.
    /// </summary>
    Task<IMailSession> OpenAsync(MailCredentials credentials, CancellationToken cancellationToken);
}
