using System.Text.RegularExpressions;

namespace Vinette.Core.Mail;

/// <summary>
/// The type a downloaded attachment is sent with. The sender's type is untrusted: a file called <c>text/html</c> or
/// <c>image/svg+xml</c> must never reach the browser with that type (it could be shown, and run, instead of saved), so
/// only a short list of types that are harmless as a download is passed on; everything else is plain bytes.
/// </summary>
public static partial class AttachmentContentType
{
    /// <summary>What an attachment is sent as when its own type is not on the list.</summary>
    public const string Bytes = "application/octet-stream";

    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "application/pdf", "application/zip", "application/gzip", "application/x-gzip", "application/x-7z-compressed",
        "application/x-rar-compressed", "application/vnd.rar", "application/x-tar", "application/msword", "application/rtf",
        "application/vnd.ms-excel", "application/vnd.ms-powerpoint", "application/vnd.ms-outlook", "application/epub+zip",
        "application/vnd.oasis.opendocument.text", "application/vnd.oasis.opendocument.spreadsheet",
        "application/vnd.oasis.opendocument.presentation",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        "image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp", "image/tiff", "image/heic", "image/avif",
        "audio/mpeg", "audio/mp4", "audio/ogg", "audio/wav", "audio/x-wav", "audio/flac", "audio/aac",
        "video/mp4", "video/mpeg", "video/quicktime", "video/webm", "video/ogg",
        "text/plain", "text/csv", "message/rfc822",
    };

    [GeneratedRegex(@"^[a-z0-9][a-z0-9!#$&^_.+-]{0,60}/[a-z0-9][a-z0-9!#$&^_.+-]{0,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex WellFormed();

    /// <summary>The type to send for an attachment the sender declared as <paramref name="declared"/>.</summary>
    public static string ForDownload(string? declared)
    {
        var type = (declared ?? string.Empty).Trim().ToLowerInvariant();
        return WellFormed().IsMatch(type) && Allowed.Contains(type) ? type : Bytes;
    }
}
