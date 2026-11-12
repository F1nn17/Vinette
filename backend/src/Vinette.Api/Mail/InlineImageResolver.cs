using Vinette.Core.Mail;

namespace Vinette.Api.Mail;

/// <summary>
/// Answers "what is the picture for this Content-ID" while the body of a mail is cleaned: the picture of the mail itself,
/// embedded as a <c>data:image</c> address. Only real pictures of the common raster kinds qualify (judged by their first
/// bytes, not by the declared type), each up to <see cref="MessageLimits.MaxInlineImageBytes"/>, and all together up to
/// <see cref="MessageLimits.MaxInlineImagesTotalBytes"/>. Embedding means the page needs no further request, which a
/// sandboxed frame without an origin could not make with the user's cookie anyway.
/// </summary>
public sealed class InlineImageResolver
{
    private readonly Dictionary<string, InlinePart> parts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> resolved = new(StringComparer.OrdinalIgnoreCase);
    private long embedded;

    public InlineImageResolver(IEnumerable<InlinePart> inlineParts)
    {
        ArgumentNullException.ThrowIfNull(inlineParts);
        foreach (var part in inlineParts)
        {
            parts.TryAdd(part.ContentId, part); // the first part with an id wins, like a browser's first match
        }
    }

    /// <summary>How many different pictures were embedded.</summary>
    public int Embedded => resolved.Values.Count(v => v is not null);

    /// <summary>The address to put into the picture for <paramref name="contentId"/> (the text after <c>cid:</c>), or null if it cannot be shown.</summary>
    public string? Resolve(string contentId)
    {
        var key = Normalize(contentId);
        if (resolved.TryGetValue(key, out var known))
        {
            return known; // the same picture twice costs its bytes once
        }

        string? uri = null;
        if (parts.TryGetValue(key, out var part) && InlineImage.ToDataUri(part.Content) is { } candidate && embedded + part.Content.Length <= MessageLimits.MaxInlineImagesTotalBytes)
        {
            embedded += part.Content.Length;
            uri = candidate;
        }

        resolved[key] = uri;
        return uri;
    }

    private static string Normalize(string contentId)
    {
        var id = contentId.Trim().Trim('<', '>');
        try
        {
            return Uri.UnescapeDataString(id); // "cid:logo%40example.org" names the part "logo@example.org"
        }
        catch (UriFormatException)
        {
            return id;
        }
    }
}
