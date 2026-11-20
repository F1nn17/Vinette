namespace Vinette.Core.Mail;

/// <summary>What became of the copy of a sent message that is kept in the "Sent" folder.</summary>
public enum SentCopyOutcome
{
    /// <summary>The copy is in the "Sent" folder.</summary>
    Saved,

    /// <summary>The mailbox has no "Sent" folder, so no copy was kept. The message itself went out.</summary>
    NoSentFolder,

    /// <summary>The copy could not be saved (the mail server failed or refused it). The message itself went out.</summary>
    Failed,
}

/// <summary>The result of sending a message: it went out, and this is what happened to its copy.</summary>
/// <param name="Message">The mail that was sent (its Message-ID, its bytes, who it went to).</param>
/// <param name="Copy">What became of the copy in the "Sent" folder.</param>
/// <param name="CopyId">The copy's id in the "Sent" folder; null unless <see cref="Copy"/> is <see cref="SentCopyOutcome.Saved"/> and the server says it.</param>
public sealed record SendOutcome(ComposedMessage Message, SentCopyOutcome Copy, MessageId? CopyId);

/// <summary>
/// Sends what the user wrote and keeps a copy: builds the mail, hands it in for sending, and, once it is out, puts a copy into the
/// "Sent" folder. Whatever happens to the copy, the message is not sent again and the result says it went out.
/// </summary>
public interface IOutgoingMail
{
    /// <summary>
    /// Sends <paramref name="message"/>. Throws what <see cref="IMessageComposer.Compose"/> and <see cref="IMailSender.SendAsync"/> throw,
    /// in which case nothing was sent (except <see cref="MailUnavailableException"/> with
    /// <see cref="MailUnavailableException.MessageMayHaveBeenSent"/>, where it may have been); once the message is out, it does not throw:
    /// a copy that could not be kept is reported in the outcome.
    /// </summary>
    Task<SendOutcome> SendAsync(MailCredentials credentials, OutgoingMessage message, CancellationToken cancellationToken);

    /// <summary>
    /// Sends a mail that was built earlier (a scheduled message, ADR-0008) and keeps a copy: the same as <see cref="SendAsync"/> after the
    /// building. Throws what <see cref="IMailSender.SendAsync"/> throws, in which case nothing was sent (except
    /// <see cref="MailUnavailableException"/> with <see cref="MailUnavailableException.MessageMayHaveBeenSent"/>).
    /// </summary>
    Task<SendOutcome> SendComposedAsync(MailCredentials credentials, ComposedMessage message, CancellationToken cancellationToken);
}
