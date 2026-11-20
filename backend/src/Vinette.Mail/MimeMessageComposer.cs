using System.Text;
using System.Text.RegularExpressions;
using MimeKit;
using MimeKit.Text;
using MimeKit.Utils;
using Vinette.Core.Mail;

namespace Vinette.Mail;

/// <summary>
/// <see cref="IMessageComposer"/> on MimeKit. Everything the user typed is data, never a header: addresses must be plain addresses,
/// names and subjects lose their line breaks, file names are cleaned, and the library encodes what needs encoding (Cyrillic in the
/// subject and in names as RFC 2047, in file names as RFC 2231). The mail is built seven-bit safe, so that it passes any relay.
/// </summary>
public sealed partial class MimeMessageComposer(TimeProvider? timeProvider = null) : IMessageComposer
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    // The way a mail goes on the wire: CRLF line ends, and a header that is too long is folded.
    private static readonly FormatOptions Wire = new FormatOptions { NewLineFormat = NewLineFormat.Dos, EnsureNewLine = true };

    // local@domain with nothing around it: no name, no angle brackets, no spaces, quotes, commas or other things that would let one
    // address become several, or a header.
    [GeneratedRegex(@"^[^\s<>@,;:""()\[\]\\]+@[^\s<>@,;:""()\[\]\\]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainAddress();

    [GeneratedRegex(@"^[^\s<>@]+@[^\s<>@]+$", RegexOptions.CultureInvariant)]
    private static partial Regex MessageIdForm();

    public ComposedMessage Compose(OutgoingMessage message) => Build(message, draft: false);

    public ComposedMessage ComposeDraft(OutgoingMessage message) => Build(message, draft: true);

    private ComposedMessage Build(OutgoingMessage message, bool draft)
    {
        ArgumentNullException.ThrowIfNull(message);

        var from = Address(message.From);
        var to = message.To.Select(Address).ToList();
        var cc = message.Cc.Select(Address).ToList();
        var bcc = message.Bcc.Select(Address).ToList();
        var recipients = to.Concat(cc).Concat(bcc)
            .Select(a => a.Address)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (recipients.Count == 0 && !draft)
        {
            throw new ArgumentException("A message needs at least one recipient.", nameof(message));
        }

        if (recipients.Count > OutgoingLimits.MaxRecipients)
        {
            throw new ArgumentException($"A message can have at most {OutgoingLimits.MaxRecipients} recipients.", nameof(message));
        }

        if ((message.Subject?.Length ?? 0) > OutgoingLimits.MaxSubjectLength)
        {
            throw new ArgumentException($"The subject can have at most {OutgoingLimits.MaxSubjectLength} characters.", nameof(message));
        }

        var mime = new MimeMessage();
        mime.From.Add(from);
        mime.To.AddRange(to);
        mime.Cc.AddRange(cc); // the hidden recipients are in the envelope only, except in a draft, which is the user's own
        if (draft)
        {
            mime.Bcc.AddRange(bcc);
        }

        mime.Subject = OneLine(message.Subject ?? string.Empty);
        mime.Date = message.Date ?? time.GetUtcNow();
        if (message.RequestReceipt)
        {
            // Asks the reader's mail program for a read receipt, sent to the sender (and to nobody else).
            mime.Headers.Add(HeaderId.DispositionNotificationTo, from.ToString(true));
        }

        var id = message.MessageId is null ? null : MessageIdOf(message.MessageId);
        if (message.MessageId is not null && id is null)
        {
            throw new ArgumentException("The Message-ID is not a Message-ID.", nameof(message));
        }

        id ??= MimeUtils.GenerateMessageId(from.Domain); // the domain of the mailbox, not the name of this machine
        mime.MessageId = id;
        Chain(mime, message);
        mime.Body = Body(message);
        mime.Prepare(EncodingConstraint.SevenBit);

        using var stream = new MemoryStream();
        mime.WriteTo(Wire, stream);
        return new ComposedMessage(stream.ToArray(), new OutgoingEnvelope(from.Address, recipients), id);
    }

    public ComposedMessage ComposeReceipt(ReceiptRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var from = Address(request.From);
        var to = Address(request.To);
        var subject = OneLine(request.OriginalSubject ?? string.Empty);
        var mime = new MimeMessage();
        mime.From.Add(from);
        mime.To.Add(to);
        mime.Subject = "Read: " + (subject.Length > 200 ? subject[..200] : subject);
        mime.Date = time.GetUtcNow();
        var id = MimeUtils.GenerateMessageId(from.Domain);
        mime.MessageId = id;
        var original = request.OriginalMessageId is null ? null : MessageIdOf(request.OriginalMessageId);
        if (original is not null)
        {
            mime.InReplyTo = original;
            mime.References.Add(original);
        }

        var sent = request.OriginalDate is { } date ? $" on {date.UtcDateTime:yyyy-MM-dd HH:mm} UTC" : string.Empty;
        var report = new MultipartReport("disposition-notification")
        {
            new TextPart("plain")
            {
                Text = $"The message sent to {from.Address}{sent} with the subject \"{subject}\" has been displayed. This is no guarantee that the message has been read or understood.",
            },
        };
        var notification = new MessageDispositionNotification();
        notification.Fields.Add("Reporting-UA", "Vinette");
        notification.Fields.Add("Final-Recipient", "rfc822; " + from.Address);
        if (original is not null)
        {
            notification.Fields.Add("Original-Message-ID", "<" + original + ">");
        }

        notification.Fields.Add("Disposition", "manual-action/MDN-sent-manually; displayed");
        report.Add(notification);
        mime.Body = report;
        mime.Prepare(EncodingConstraint.SevenBit);

        using var stream = new MemoryStream();
        mime.WriteTo(Wire, stream);
        return new ComposedMessage(stream.ToArray(), new OutgoingEnvelope(from.Address, [to.Address]), id);
    }

    private static MailboxAddress Address(MailAddressInfo person)
    {
        ArgumentNullException.ThrowIfNull(person);
        var address = (person.Address ?? string.Empty).Trim();
        if (!PlainAddress().IsMatch(address) || address.Length > 254 || address.Any(char.IsControl))
        {
            throw new ArgumentException("An address is not an address.", nameof(person));
        }

        var name = OneLine(person.Name ?? string.Empty).Trim();
        return new MailboxAddress(name.Length == 0 ? null : name, address);
    }

    /// <summary>Text on one line: every control character (a line break above all) becomes a space, so that nothing typed can start a header.</summary>
    private static string OneLine(string text)
    {
        var line = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            _ = line.Append(char.IsControl(c) ? ' ' : c);
        }

        return line.ToString();
    }

    private static string? MessageIdOf(string value)
    {
        var id = value.Trim().TrimStart('<').TrimEnd('>').Trim();
        return id.Length is > 0 and <= 255 && MessageIdForm().IsMatch(id) && !id.Any(char.IsControl) ? id : null;
    }

    // A reply joins the conversation: In-Reply-To names the message, References the whole thread (first and latest ones kept).
    // The ids come from other people's mail, so one that is malformed is left out instead of failing the send.
    private static void Chain(MimeMessage mime, OutgoingMessage message)
    {
        var replyTo = message.InReplyTo is null ? null : MessageIdOf(message.InReplyTo);
        var thread = (message.References ?? [])
            .Select(MessageIdOf)
            .OfType<string>()
            .Where(r => !string.Equals(r, mime.MessageId, StringComparison.Ordinal))
            .ToList();
        if (replyTo is not null)
        {
            mime.InReplyTo = replyTo;
            if (!thread.Contains(replyTo, StringComparer.Ordinal))
            {
                thread.Add(replyTo);
            }
        }

        if (thread.Count > OutgoingLimits.MaxReferences)
        {
            thread = [thread[0], .. thread.Skip(thread.Count - (OutgoingLimits.MaxReferences - 1))];
        }

        foreach (var reference in thread.Distinct(StringComparer.Ordinal))
        {
            mime.References.Add(reference);
        }
    }

    private static MimeEntity Body(OutgoingMessage message)
    {
        var html = string.IsNullOrWhiteSpace(message.Html) ? null : message.Html;
        var text = message.Text;
        if (string.IsNullOrEmpty(text) && html is not null)
        {
            text = new HtmlToText().Convert(html); // a mail with only HTML looks like spam to many filters: give it a text version
        }

        var plain = new TextPart(TextFormat.Plain) { Text = text ?? string.Empty };
        MimeEntity body = plain;
        if (html is not null)
        {
            body = new Multipart("alternative") { plain, new TextPart(TextFormat.Html) { Text = html } };
        }

        if (message.Attachments.Count == 0)
        {
            return body;
        }

        var mixed = new Multipart("mixed") { body };
        var number = 0;
        foreach (var file in message.Attachments)
        {
            number++;
            mixed.Add(new MimePart(ContentType.Parse(AttachmentContentType.ForDownload(file.ContentType)))
            {
                Content = new MimeContent(new MemoryStream(file.Content.ToArray(), writable: false)),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment)
                {
                    FileName = AttachmentFileName.Sanitize(file.FileName, $"attachment-{number}"),
                },
                ContentTransferEncoding = ContentEncoding.Base64,
            });
        }

        return mixed;
    }
}
