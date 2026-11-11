using System.Globalization;
using MimeKit;
using Vinette.Core.Mail;

namespace Vinette.Mail;

/// <summary>
/// Takes a mail apart with MimeKit: picks the bodies, lists attachments and inline parts. The mail is untrusted, so the
/// walk down the tree is bounded (depth and number of parts) and a broken mail gives what could be read, never an exception.
/// </summary>
public sealed class MimeMessageParser : IMessageParser
{
    /// <summary>How deep the tree of parts is followed; what is deeper is ignored.</summary>
    public const int MaxDepth = 32;

    /// <summary>How many parts are looked at; the rest is ignored.</summary>
    public const int MaxParts = 2000;

    public async Task<ParsedMessage> ParseAsync(Stream source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        MimeMessage message;
        try
        {
            message = await MimeMessage.LoadAsync(source, cancellationToken).ConfigureAwait(false);
        }
        catch (FormatException)
        {
            // Not a mail at all (empty, cut off before the first header): an empty message, not an error page.
            return new ParsedMessage(string.Empty, null, [], [], null, null, null, null, [], [], []);
        }

        return await ParseAsync(message, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<ParsedMessage> ParseAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        var text = message.TextBody;
        var html = message.HtmlBody;

        // The parts MimeKit took the bodies from are not attachments.
        var bodies = new List<TextPart>(2);
        if (!string.IsNullOrEmpty(text) && message.BodyParts.OfType<TextPart>().FirstOrDefault(p => !p.IsHtml && p.Text == text) is { } plain)
        {
            bodies.Add(plain);
        }

        if (!string.IsNullOrEmpty(html) && message.BodyParts.OfType<TextPart>().FirstOrDefault(p => p.IsHtml && p.Text == html) is { } markup)
        {
            bodies.Add(markup);
        }

        var attachments = new List<MessageAttachment>();
        var inline = new List<InlinePart>();
        var counter = new Counter();
        if (message.Body is { } body)
        {
            await WalkAsync(body, "1", 1, bodies, attachments, inline, counter, cancellationToken).ConfigureAwait(false);
        }

        return new ParsedMessage(
            message.Subject ?? string.Empty,
            message.From.Mailboxes.Select(ToAddress).FirstOrDefault(),
            [.. message.To.Mailboxes.Select(ToAddress)],
            [.. message.Cc.Mailboxes.Select(ToAddress)],
            message.Date == DateTimeOffset.MinValue ? null : message.Date,
            string.IsNullOrWhiteSpace(message.MessageId) ? null : message.MessageId,
            string.IsNullOrEmpty(text) ? null : text,
            string.IsNullOrEmpty(html) ? null : html,
            attachments,
            inline,
            [.. message.Headers.Where(h => h.Id == HeaderId.AuthenticationResults).Select(h => h.Value)]);
    }

    private sealed class Counter
    {
        public int Parts { get; set; }
    }

    private static async Task WalkAsync(
        MimeEntity entity,
        string id,
        int depth,
        List<TextPart> bodies,
        List<MessageAttachment> attachments,
        List<InlinePart> inline,
        Counter counter,
        CancellationToken cancellationToken)
    {
        if (depth > MaxDepth || ++counter.Parts > MaxParts)
        {
            return;
        }

        switch (entity)
        {
            case Multipart multipart:
                for (var i = 0; i < multipart.Count; i++)
                {
                    var childId = $"{id}.{(i + 1).ToString(CultureInfo.InvariantCulture)}";
                    await WalkAsync(multipart[i], childId, depth + 1, bodies, attachments, inline, counter, cancellationToken).ConfigureAwait(false);
                }

                break;

            case MessagePart part:
                // An attached mail is one attachment; its insides are not mixed into this message.
                var inner = part.Message?.Subject;
                var name = AttachmentFileName.Sanitize(
                    FileNameOf(part) ?? (string.IsNullOrWhiteSpace(inner) ? null : inner + ".eml"),
                    $"message-{id}.eml");
                var messageSize = await MessageSizeAsync(part, cancellationToken).ConfigureAwait(false);
                attachments.Add(new MessageAttachment(id, name, part.ContentType.MimeType.ToLowerInvariant(), messageSize, true));
                break;

            case TextPart text when bodies.Contains(text):
                break; // the body itself

            case MimePart leaf:
                var size = await DecodedSizeAsync(leaf, cancellationToken).ConfigureAwait(false);
                var type = leaf.ContentType.MimeType.ToLowerInvariant();
                var contentId = Clean(leaf.ContentId);
                var isAttachment = leaf.ContentDisposition?.Disposition == ContentDisposition.Attachment;
                if (contentId is not null && !isAttachment)
                {
                    inline.Add(new InlinePart(id, contentId, type, size));
                }
                else
                {
                    var fallback = $"attachment-{id}{ExtensionOf(type)}";
                    attachments.Add(new MessageAttachment(id, AttachmentFileName.Sanitize(FileNameOf(leaf), fallback), type, size, false));
                }

                break;

            default:
                break;
        }
    }

    private static string? FileNameOf(MimeEntity entity)
    {
        var name = entity.ContentDisposition?.FileName ?? entity.ContentType.Name;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static string? Clean(string? contentId) => string.IsNullOrWhiteSpace(contentId) ? null : contentId.Trim().Trim('<', '>');

    private static string ExtensionOf(string contentType) => contentType switch
    {
        "text/plain" => ".txt",
        "text/html" => ".html",
        _ => string.Empty,
    };

    private static async Task<long> DecodedSizeAsync(MimePart part, CancellationToken cancellationToken)
    {
        if (part.Content is null)
        {
            return 0;
        }

        await using var counting = new CountingStream();
        await part.Content.DecodeToAsync(counting, cancellationToken).ConfigureAwait(false);
        return counting.Length;
    }

    private static async Task<long> MessageSizeAsync(MessagePart part, CancellationToken cancellationToken)
    {
        if (part.Message is null)
        {
            return 0;
        }

        await using var counting = new CountingStream();
        await part.Message.WriteToAsync(counting, cancellationToken).ConfigureAwait(false);
        return counting.Length;
    }

    private static MailAddressInfo ToAddress(MailboxAddress mailbox) => new(mailbox.Name ?? string.Empty, mailbox.Address);

    /// <summary>A stream that throws everything away and only counts.</summary>
    private sealed class CountingStream : Stream
    {
        private long length;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => length;

        public override long Position
        {
            get => length;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => length += count;

        public override void Write(ReadOnlySpan<byte> buffer) => length += buffer.Length;

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            length += count;
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            length += buffer.Length;
            return ValueTask.CompletedTask;
        }
    }
}
