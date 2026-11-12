namespace Vinette.Core.Mail;

/// <summary>The result of cleaning the HTML of a mail.</summary>
/// <param name="Html">
/// Markup that is safe to put into a sandboxed frame: no scripts, no active content, no forms, no external loads. Empty
/// if the mail was <see cref="Rejected"/>.
/// </param>
/// <param name="BlockedExternalResources">
/// How many things that would have loaded something from the internet (images, backgrounds, style sheets, fonts) were
/// taken out. More than zero is the reason to offer "show external images".
/// </param>
/// <param name="Truncated">The mail was longer than <see cref="IMessageHtmlSanitizer.MaxInputLength"/>; only the beginning was cleaned.</param>
/// <param name="Rejected">
/// The markup was refused as a whole (it is nested so deeply that no real mail is): show the text version instead.
/// </param>
public sealed record SanitizedHtml(string Html, int BlockedExternalResources, bool Truncated, bool Rejected)
{
    /// <summary>The result for a mail without HTML, and for a rejected one.</summary>
    public static SanitizedHtml Empty { get; } = new(string.Empty, 0, false, false);
}

/// <summary>Cleans the HTML of a mail so that it can be shown. The mail is hostile until proven otherwise.</summary>
public interface IMessageHtmlSanitizer
{
    /// <summary>The longest HTML that is cleaned, in characters; the rest is cut off (see <see cref="SanitizedHtml.Truncated"/>).</summary>
    const int MaxInputLength = 1_000_000;

    /// <summary>The deepest nesting of elements that is accepted; deeper markup is <see cref="SanitizedHtml.Rejected"/>.</summary>
    const int MaxNesting = 200;

    /// <summary>The biggest <c>data:</c> image that is kept, in decoded bytes.</summary>
    const int MaxDataImageBytes = 1024 * 1024;

    /// <summary>Cleans <paramref name="html"/>; <c>cid:</c> pictures are left as they are. Never throws for any input.</summary>
    SanitizedHtml Sanitize(string? html) => Sanitize(html, null);

    /// <summary>
    /// Cleans <paramref name="html"/>. A <c>cid:</c> picture is replaced by what <paramref name="resolveContentId"/> returns
    /// for its Content-ID (text of a <c>data:image</c> address built by the application); when it returns null, or there is
    /// no resolver, the picture's address is taken out (with a resolver) or kept as it is (without). Never throws for any input.
    /// </summary>
    SanitizedHtml Sanitize(string? html, Func<string, string?>? resolveContentId);
}
