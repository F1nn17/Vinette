namespace Vinette.Core.Mail;

/// <summary>
/// Makes a picture of a mail ready to be embedded in the body as a <c>data:</c> address. What the picture is, is decided
/// by its first bytes and never by the type the sender declared: a part that says <c>image/png</c> and is a page of HTML
/// is not an image. SVG is never embedded (it can carry scripts); only the common raster formats are.
/// </summary>
public static class InlineImage
{
    /// <summary>The <c>data:image/…;base64,…</c> address for <paramref name="content"/>, or null if it is not a safe picture or is too big.</summary>
    public static string? ToDataUri(ReadOnlySpan<byte> content)
    {
        if (content.Length is 0 or > MessageLimits.MaxInlineImageBytes)
        {
            return null;
        }

        var type = Sniff(content);
        return type is null ? null : $"data:{type};base64,{Convert.ToBase64String(content)}";
    }

    /// <summary>The media type of a PNG, JPEG, GIF or WebP picture by its signature; null for anything else.</summary>
    public static string? Sniff(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "image/png";
        }

        if (content.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return "image/jpeg";
        }

        if (content.StartsWith("GIF87a"u8) || content.StartsWith("GIF89a"u8))
        {
            return "image/gif";
        }

        if (content.Length >= 12 && content[..4].SequenceEqual("RIFF"u8) && content.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        return null;
    }
}
