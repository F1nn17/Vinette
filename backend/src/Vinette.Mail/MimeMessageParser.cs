using System.Globalization;
using System.Text;
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

    static MimeMessageParser() => LegacyCharsets.Ensure();

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

    public async Task<AttachmentContent?> ExtractAttachmentAsync(Stream source, string partId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(partId);
        MimeMessage message;
        try
        {
            message = await MimeMessage.LoadAsync(source, cancellationToken).ConfigureAwait(false);
        }
        catch (FormatException)
        {
            return null;
        }

        // The same walk that lists the attachments, so that only what is listed can be taken, under the same names.
        var entities = new Dictionary<string, MimeEntity>(StringComparer.Ordinal);
        var parsed = await ParseAsync(message, cancellationToken, entities).ConfigureAwait(false);
        var attachment = parsed.Attachments.FirstOrDefault(a => a.PartId == partId);
        if (attachment is null || !entities.TryGetValue(partId, out var entity))
        {
            return null;
        }

        using var buffer = new MemoryStream();
        switch (entity)
        {
            case MessagePart part when part.Message is not null:
                await part.Message.WriteToAsync(buffer, cancellationToken).ConfigureAwait(false);
                break;
            case MimePart { Content: not null } leaf:
                await leaf.Content.DecodeToAsync(buffer, cancellationToken).ConfigureAwait(false);
                break;
        }

        return new AttachmentContent(attachment, buffer.ToArray());
    }

    internal static async Task<ParsedMessage> ParseAsync(MimeMessage message, CancellationToken cancellationToken, Dictionary<string, MimeEntity>? entities = null)
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

        // Old and careless programs label 8-bit text "us-ascii" (or nothing); the label would turn it into question marks.
        foreach (var part in bodies)
        {
            if (await RecodeMislabelledAsync(part, cancellationToken).ConfigureAwait(false) is { } recoded)
            {
                if (part.IsHtml)
                {
                    html = recoded;
                }
                else
                {
                    text = recoded;
                }
            }
        }

        var attachments = new List<MessageAttachment>();
        var inline = new List<InlinePart>();
        var counter = new Counter();
        if (message.Body is { } body)
        {
            await WalkAsync(body, "1", 1, bodies, attachments, inline, entities, counter, cancellationToken).ConfigureAwait(false);
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

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// The text of a part whose charset is missing or "us-ascii" but whose bytes are not ASCII: read as UTF-8 if the bytes
    /// are valid UTF-8, else as windows-1252. Null if the label can be trusted (or the part is plain ASCII).
    /// </summary>
    private static async Task<string?> RecodeMislabelledAsync(TextPart part, CancellationToken cancellationToken)
    {
        var charset = part.ContentType.Charset;
        if (part.Content is null || (!string.IsNullOrWhiteSpace(charset) && !charset.Equals("us-ascii", StringComparison.OrdinalIgnoreCase) && !charset.Equals("ascii", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        using var buffer = new MemoryStream();
        await part.Content.DecodeToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        if (!bytes.ContainsAnyInRange((byte)0x80, (byte)0xFF))
        {
            return null;
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
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
        Dictionary<string, MimeEntity>? entities,
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
                    await WalkAsync(multipart[i], childId, depth + 1, bodies, attachments, inline, entities, counter, cancellationToken).ConfigureAwait(false);
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
                entities?.Add(id, part);
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
                    var content = size <= MessageLimits.MaxInlineImageBytes ? await DecodedBytesAsync(leaf, cancellationToken).ConfigureAwait(false) : [];
                    inline.Add(new InlinePart(id, contentId, type, size, content));
                }
                else
                {
                    var fallback = $"attachment-{id}{ExtensionOf(type)}";
                    attachments.Add(new MessageAttachment(id, AttachmentFileName.Sanitize(FileNameOf(leaf), fallback), type, size, false));
                    entities?.Add(id, leaf);
                }

                break;

            default:
                break;
        }
    }

    private static string? FileNameOf(MimeEntity entity)
    {
        // MimeKit gives an empty string, not null, when the parameter is missing: both places are tried.
        var name = entity.ContentDisposition?.FileName;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = entity.ContentType.Name;
        }

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

    private static async Task<byte[]> DecodedBytesAsync(MimePart part, CancellationToken cancellationToken)
    {
        if (part.Content is null)
        {
            return [];
        }

        using var buffer = new MemoryStream();
        await part.Content.DecodeToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
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
