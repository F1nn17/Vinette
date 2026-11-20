namespace Vinette.Core.Mail;

/// <summary>A file attached to a message that is being written.</summary>
/// <param name="FileName">The name the recipient sees; cleaned (no path, no control characters) when the message is built.</param>
/// <param name="ContentType">The type of the file ("application/pdf"); "application/octet-stream" when unknown or unusable.</param>
/// <param name="Content">The bytes of the file.</param>
public sealed record OutgoingAttachment(string FileName, string ContentType, ReadOnlyMemory<byte> Content);

/// <summary>A message the user has written, before it is built into a mail.</summary>
/// <param name="From">The sender: the signed-in mailbox (the display name may be the user's own).</param>
/// <param name="To">The main recipients.</param>
/// <param name="Cc">The copy recipients, named in the message.</param>
/// <param name="Bcc">The hidden recipients: delivered to, named nowhere in the message.</param>
/// <param name="Subject">The subject, any text (it is encoded when it needs to be).</param>
/// <param name="Text">The plain text; if only <paramref name="Html"/> is given, a text version is made from it.</param>
/// <param name="Html">The HTML body; may be absent for a plain text message.</param>
/// <param name="Attachments">The attached files.</param>
/// <param name="InReplyTo">The Message-ID of the message this is a reply to (with or without angle brackets).</param>
/// <param name="References">The Message-IDs of the conversation so far, oldest first; the reply's <paramref name="InReplyTo"/> is added at the end if it is missing.</param>
/// <param name="MessageId">A Message-ID to keep (a draft saved again keeps its own); a new one is made if absent.</param>
/// <param name="Date">When the message was written; now if absent.</param>
/// <param name="RequestReceipt">Ask the recipients' mail programs for a read receipt (a Disposition-Notification-To header naming the sender, RFC 8098). Off by default.</param>
public sealed record OutgoingMessage(
    MailAddressInfo From,
    IReadOnlyList<MailAddressInfo> To,
    IReadOnlyList<MailAddressInfo> Cc,
    IReadOnlyList<MailAddressInfo> Bcc,
    string Subject,
    string? Text,
    string? Html,
    IReadOnlyList<OutgoingAttachment> Attachments,
    string? InReplyTo = null,
    IReadOnlyList<string>? References = null,
    string? MessageId = null,
    DateTimeOffset? Date = null,
    bool RequestReceipt = false);

/// <summary>A read receipt to send: the reader of a message tells its sender that it was displayed (RFC 8098).</summary>
/// <param name="From">The mailbox that read the message.</param>
/// <param name="To">Where the message asked the receipt to go (the sender).</param>
/// <param name="OriginalMessageId">The Message-ID of the message that was read, if it has one.</param>
/// <param name="OriginalSubject">Its subject.</param>
/// <param name="OriginalDate">When it was sent, if known.</param>
public sealed record ReceiptRequest(MailAddressInfo From, MailAddressInfo To, string? OriginalMessageId, string OriginalSubject, DateTimeOffset? OriginalDate);

/// <summary>A message built into a mail, ready to be sent or kept as a draft.</summary>
/// <param name="Raw">The whole mail as it goes on the wire (RFC 5322, seven-bit safe).</param>
/// <param name="Envelope">Who it is sent from and to, hidden recipients included.</param>
/// <param name="MessageId">The Message-ID of the mail, without angle brackets.</param>
public sealed record ComposedMessage(ReadOnlyMemory<byte> Raw, OutgoingEnvelope Envelope, string MessageId);

/// <summary>Limits of what can be written, so that one request cannot make the server build or send something absurd.</summary>
public static class OutgoingLimits
{
    /// <summary>The most recipients (To, Cc and Bcc together) of one message.</summary>
    public const int MaxRecipients = 100;

    /// <summary>The longest subject, in characters.</summary>
    public const int MaxSubjectLength = 998;

    /// <summary>How many Message-IDs the References header keeps: the first one and the latest ones (long threads would otherwise grow without end).</summary>
    public const int MaxReferences = 20;
}

/// <summary>
/// Builds a mail out of what the user wrote: encodes what needs encoding (Cyrillic in the subject, in names and in file names),
/// makes the structure (text and HTML as alternatives, attachments around them), gives the message an id, and chains replies to
/// the conversation. The result goes to <see cref="IMailSender"/> or into the drafts folder.
/// </summary>
public interface IMessageComposer
{
    /// <summary>
    /// Builds the mail. Throws <see cref="ArgumentException"/> for what cannot be built: no recipient, too many recipients
    /// (<see cref="OutgoingLimits.MaxRecipients"/>), an address that is not an address, a subject that is too long. Nothing the
    /// user typed can add a header (line breaks in a subject or a name are not carried into the mail).
    /// </summary>
    ComposedMessage Compose(OutgoingMessage message);

    /// <summary>
    /// Builds the mail as a draft to be kept in the drafts folder (not sent): the same checks and encoding, but nobody has to be named
    /// yet, and the hidden recipients are written into the draft (it is the user's own copy; <see cref="ComposedMessage.Envelope"/> is
    /// not for sending). Throws <see cref="ArgumentException"/> for what cannot be built.
    /// </summary>
    ComposedMessage ComposeDraft(OutgoingMessage message);

    /// <summary>
    /// Builds a read receipt (a <c>multipart/report</c> with a <c>message/disposition-notification</c>, RFC 8098) from the reader to the sender,
    /// "displayed", sent by hand. Throws <see cref="ArgumentException"/> if an address is not an address.
    /// </summary>
    ComposedMessage ComposeReceipt(ReceiptRequest request);

    /// <summary>
    /// The mail with its hidden recipients written into it as a <c>Bcc</c> header, so that it can be kept as a draft (the sent form has no
    /// such header; ADR-0008). Everything else is as it was.
    /// </summary>
    ReadOnlyMemory<byte> AddHiddenRecipients(ReadOnlyMemory<byte> raw, IReadOnlyList<MailAddressInfo> hidden);
}
