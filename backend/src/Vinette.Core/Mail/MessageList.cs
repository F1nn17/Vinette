using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Vinette.Core.Mail;

/// <summary>A person on a message: the display name (may be empty) and the address.</summary>
public sealed record MailAddressInfo(string Name, string Address);

/// <summary>One line of the message list.</summary>
/// <param name="Id">The message's identifier.</param>
/// <param name="From">The sender; null if the message has none.</param>
/// <param name="To">The recipients (the first few: enough for a "Sent" list).</param>
/// <param name="Subject">The subject, decoded; empty if none.</param>
/// <param name="Date">When the message was written, or, if it says nothing sensible, when the server received it.</param>
/// <param name="Seen">Read.</param>
/// <param name="Flagged">Marked with a flag ("starred").</param>
/// <param name="Answered">Replied to.</param>
/// <param name="Draft">A draft.</param>
/// <param name="HasAttachments">Has at least one attached file.</param>
/// <param name="Labels">The message's own keywords (labels), without the system ones that begin with "$" or "\".</param>
/// <param name="Size">The size on the server, in bytes.</param>
/// <param name="Preview">The beginning of the text, one line, at most <see cref="PreviewText.MaxLength"/> characters; empty if there is none or it was not asked for.</param>
public sealed record MessageListItem(
    MessageId Id,
    MailAddressInfo? From,
    IReadOnlyList<MailAddressInfo> To,
    string Subject,
    DateTimeOffset Date,
    bool Seen,
    bool Flagged,
    bool Answered,
    bool Draft,
    bool HasAttachments,
    IReadOnlyList<string> Labels,
    long Size,
    string Preview);

/// <summary>
/// A position in a message list: the last message the client has, so the next page starts right after it. It names the
/// folder's <c>UIDVALIDITY</c> too, so a cursor of a recreated folder is refused. Text form: <c>uidValidity.uid</c>.
/// </summary>
public readonly record struct MessageCursor
{
    public MessageCursor(uint uidValidity, uint uid)
    {
        ArgumentOutOfRangeException.ThrowIfZero(uidValidity);
        ArgumentOutOfRangeException.ThrowIfZero(uid);
        UidValidity = uidValidity;
        Uid = uid;
    }

    public uint UidValidity { get; }

    public uint Uid { get; }

    public static bool TryParse([NotNullWhen(true)] string? text, out MessageCursor cursor)
    {
        cursor = default;
        var parts = text?.Split('.');
        if (parts is not { Length: 2 } || !TryNumber(parts[0], out var validity) || !TryNumber(parts[1], out var uid))
        {
            return false;
        }

        cursor = new MessageCursor(validity, uid);
        return true;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{UidValidity}.{Uid}");

    private static bool TryNumber(string text, out uint value)
    {
        value = 0;
        return text.Length is > 0 and <= 10
            && text.All(char.IsAsciiDigit)
            && (text.Length == 1 || text[0] != '0')
            && uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value != 0;
    }
}

/// <summary>Which page of a folder to list.</summary>
/// <param name="Folder">The folder's path.</param>
/// <param name="After">Where the previous page ended; null for the first page.</param>
/// <param name="Limit">How many messages at most, see <see cref="MaxLimit"/>.</param>
/// <param name="NewestFirst">Newest arrived first (the default of a mailbox) or oldest first.</param>
/// <param name="IncludePreview">Fetch the text preview; costs the server a look into every message, so the compact list leaves it out.</param>
public sealed record MessageListQuery(string Folder, MessageCursor? After, int Limit, bool NewestFirst = true, bool IncludePreview = true)
{
    public const int DefaultLimit = 50;

    public const int MaxLimit = 100;
}

/// <summary>A page of the message list. Messages are ordered by arrival (UID), which never changes for a message.</summary>
/// <param name="UidValidity">The folder's <c>UIDVALIDITY</c> the page was read with.</param>
/// <param name="Items">The messages, in the requested order.</param>
/// <param name="Next">Pass as <see cref="MessageListQuery.After"/> for the next page; null on the last page.</param>
/// <param name="Total">Messages in the whole folder.</param>
public sealed record MessagePage(uint UidValidity, IReadOnlyList<MessageListItem> Items, MessageCursor? Next, int Total);
