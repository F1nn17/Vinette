namespace Vinette.Core.Mail;

/// <summary>
/// Who a message goes from and to on the wire, apart from what its headers say: <c>Bcc</c> recipients are here and nowhere in the
/// message, and the sender must be the signed-in mailbox (a mail server that checks the sender refuses anything else).
/// </summary>
/// <param name="From">The mailbox the message is sent from: the signed-in one.</param>
/// <param name="Recipients">Everybody the server has to deliver to: To, Cc and Bcc together; at least one.</param>
public sealed record OutgoingEnvelope(string From, IReadOnlyList<string> Recipients);

/// <summary>Why a mail server refused to take a message.</summary>
public enum SendRejection
{
    /// <summary>A recipient address was refused (no such mailbox, not allowed).</summary>
    Recipient,

    /// <summary>The sender was refused (the address is not the mailbox that signed in, or may not send).</summary>
    Sender,

    /// <summary>The message is bigger than the server accepts.</summary>
    TooLarge,

    /// <summary>The server did not like the message itself (rejected as spam, malformed, a policy).</summary>
    Content,

    /// <summary>Refused, and nothing more can be said.</summary>
    Other,
}

/// <summary>
/// The mail server refused to take the message (a 4xx or 5xx answer to an address or to the message). Nothing was sent. The
/// server's own words are in <see cref="Exception.InnerException"/> for the log only: they may name addresses.
/// </summary>
public sealed class MailRejectedException : Exception
{
    public MailRejectedException(SendRejection reason, string message, Exception? innerException = null)
        : base(message, innerException) => Reason = reason;

    /// <summary>What was refused.</summary>
    public SendRejection Reason { get; }

    /// <summary>The recipient that was refused, if the server said which (for <see cref="SendRejection.Recipient"/>).</summary>
    public string? Address { get; init; }
}

/// <summary>
/// Sends mail through the mail server's submission service on behalf of a user (SMTP in Vinette.Mail). Sending never repeats by
/// itself: a message the server may already have taken must not go out twice, so a failure is reported and the caller decides.
/// </summary>
public interface IMailSender
{
    /// <summary>
    /// Sends <paramref name="message"/> (a whole RFC 5322 message, as built by the composer) to the recipients of the
    /// <paramref name="envelope"/>, signing in with the user's own address and password. Throws:
    /// <see cref="MailAccessRevokedException"/> if the server rejects the password,
    /// <see cref="MailRejectedException"/> if it refuses an address or the message (nothing was sent),
    /// <see cref="MailUnavailableException"/> if it cannot be reached or the connection broke; check
    /// <see cref="MailUnavailableException.MessageMayHaveBeenSent"/> before sending again.
    /// </summary>
    Task SendAsync(MailCredentials credentials, OutgoingEnvelope envelope, ReadOnlyMemory<byte> message, CancellationToken cancellationToken);
}
