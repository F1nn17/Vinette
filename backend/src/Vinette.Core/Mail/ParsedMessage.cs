namespace Vinette.Core.Mail;

/// <summary>An attached file (or an attached message) of a mail.</summary>
/// <param name="PartId">The part's address in the message: numbers of the parts down the tree, "1.2.1" (the IMAP section number).</param>
/// <param name="FileName">A safe plain file name: no path, no control or direction-changing characters, never empty.</param>
/// <param name="ContentType">The declared type, lower case ("application/pdf"). Untrusted: never decide what to do with the bytes by it.</param>
/// <param name="Size">The size of the content after decoding, in bytes.</param>
/// <param name="IsMessage">An attached mail (<c>message/rfc822</c>).</param>
public sealed record MessageAttachment(string PartId, string FileName, string ContentType, long Size, bool IsMessage);

/// <summary>A part the HTML body refers to by <c>cid:</c>: an inline image, not a file to download.</summary>
/// <param name="PartId">The part's address, see <see cref="MessageAttachment.PartId"/>.</param>
/// <param name="ContentId">The Content-ID without angle brackets.</param>
/// <param name="ContentType">The declared type, lower case.</param>
/// <param name="Size">The size after decoding, in bytes.</param>
public sealed record InlinePart(string PartId, string ContentId, string ContentType, long Size);

/// <summary>
/// A mail taken apart: headers, the bodies and the lists of attachments and inline parts. Everything here comes from the
/// sender and is untrusted; <see cref="Html"/> in particular is raw and must go through the sanitizer before anyone sees it.
/// </summary>
/// <param name="Subject">The subject, decoded; empty if there is none. A subject that cannot be decoded stays as written.</param>
/// <param name="From">The first sender; null if none.</param>
/// <param name="To">The recipients (all of them, groups flattened).</param>
/// <param name="Cc">The copy recipients.</param>
/// <param name="Date">The Date header; null if it is absent or unreadable.</param>
/// <param name="MessageId">The Message-ID header without angle brackets; null if absent.</param>
/// <param name="Text">The plain text body; null if the mail has none.</param>
/// <param name="Html">The HTML body, raw; null if the mail has none.</param>
/// <param name="Attachments">The attached files, in the order of the message.</param>
/// <param name="InlineParts">The parts referred to by <c>cid:</c>.</param>
/// <param name="AuthenticationResults">The values of the Authentication-Results headers, as written (for the sender check).</param>
public sealed record ParsedMessage(
    string Subject,
    MailAddressInfo? From,
    IReadOnlyList<MailAddressInfo> To,
    IReadOnlyList<MailAddressInfo> Cc,
    DateTimeOffset? Date,
    string? MessageId,
    string? Text,
    string? Html,
    IReadOnlyList<MessageAttachment> Attachments,
    IReadOnlyList<InlinePart> InlineParts,
    IReadOnlyList<string> AuthenticationResults);

/// <summary>Takes a raw mail (RFC 5322 with MIME) apart. Never throws on a broken mail: it returns what it could read.</summary>
public interface IMessageParser
{
    /// <summary>Parses the mail in <paramref name="source"/>, which is read to its end.</summary>
    Task<ParsedMessage> ParseAsync(Stream source, CancellationToken cancellationToken);
}
