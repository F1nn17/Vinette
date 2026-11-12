namespace Vinette.Core.Mail;

/// <summary>A message as the mail server holds it: the raw bytes of the mail and its flags.</summary>
/// <param name="Id">The message's identifier.</param>
/// <param name="Content">The whole mail, headers and parts, as received (RFC 5322 with MIME).</param>
/// <param name="Seen">Read.</param>
/// <param name="Flagged">Marked with a flag.</param>
/// <param name="Answered">Replied to.</param>
/// <param name="Draft">A draft.</param>
/// <param name="Labels">The message's own keywords (labels), without the system ones.</param>
public sealed record MessageSource(MessageId Id, byte[] Content, bool Seen, bool Flagged, bool Answered, bool Draft, IReadOnlyList<string> Labels);

/// <summary>A change of a message's flags; only what is set changes.</summary>
/// <param name="Seen">Read (true) or unread (false); null leaves it as it is.</param>
/// <param name="Flagged">Marked with a flag (true) or not (false); null leaves it as it is.</param>
public sealed record MessageFlagChange(bool? Seen, bool? Flagged)
{
    /// <summary>Whether the change changes anything.</summary>
    public bool IsEmpty => Seen is null && Flagged is null;
}

/// <summary>The flags a message has.</summary>
public sealed record MessageFlagState(bool Seen, bool Flagged, bool Answered, bool Draft);

/// <summary>Limits on what is read from the mail server for the viewer.</summary>
public static class MessageLimits
{
    /// <summary>
    /// The biggest mail that is loaded to be shown, in bytes. A bigger one is refused (<see cref="MailMessageTooLargeException"/>)
    /// without being downloaded: the viewer keeps the whole mail in memory to take it apart.
    /// </summary>
    public const long MaxViewableBytes = 32L * 1024 * 1024;

    /// <summary>The biggest picture of a mail that is embedded into its body, in bytes; a bigger one is not shown.</summary>
    public const int MaxInlineImageBytes = 1024 * 1024;

    /// <summary>The most picture bytes that are embedded into one body; the pictures after that are not shown.</summary>
    public const int MaxInlineImagesTotalBytes = 8 * 1024 * 1024;
}

/// <summary>The message is not (or no longer) in the folder: deleted or moved meanwhile, or the id never existed.</summary>
public sealed class MailMessageNotFoundException : Exception
{
    public MailMessageNotFoundException()
    {
    }

    public MailMessageNotFoundException(string message)
        : base(message)
    {
    }

    public MailMessageNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The mail is larger than <see cref="MessageLimits.MaxViewableBytes"/> and is not loaded.</summary>
public sealed class MailMessageTooLargeException : Exception
{
    public MailMessageTooLargeException()
    {
    }

    public MailMessageTooLargeException(string message)
        : base(message)
    {
    }

    public MailMessageTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public MailMessageTooLargeException(long size)
        : base("The message is too large to be shown.") => Size = size;

    /// <summary>The size of the mail on the server, in bytes (0 if unknown).</summary>
    public long Size { get; }
}
