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
/// <param name="ExternalImages">
/// The addresses of the external pictures (<c>img src</c> on the web) the body asks for, once each, at most
/// <see cref="IMessageHtmlSanitizer.MaxExternalImages"/>; what the reader can be offered to load.
/// </param>
public sealed record SanitizedHtml(string Html, int BlockedExternalResources, bool Truncated, bool Rejected, IReadOnlyList<string>? ExternalImages = null)
{
    /// <summary>The external pictures (never null).</summary>
    public IReadOnlyList<string> ExternalImageList => ExternalImages ?? [];

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

    /// <summary>How many external picture addresses are reported (and so can be loaded) for one body.</summary>
    const int MaxExternalImages = 30;

    /// <summary>The biggest <c>data:</c> image that is kept, in decoded bytes.</summary>
    const int MaxDataImageBytes = 1024 * 1024;

    /// <summary>Cleans <paramref name="html"/>; <c>cid:</c> pictures are left as they are. Never throws for any input.</summary>
    SanitizedHtml Sanitize(string? html) => Sanitize(html, null, MaxInputLength);

    /// <summary>Cleans <paramref name="html"/>, resolving <c>cid:</c> pictures; at most <see cref="MaxInputLength"/> characters.</summary>
    SanitizedHtml Sanitize(string? html, Func<string, string?>? resolveContentId) => Sanitize(html, resolveContentId, MaxInputLength);

    /// <summary>
    /// Cleans <paramref name="html"/>. A <c>cid:</c> picture is replaced by what <paramref name="resolveContentId"/> returns
    /// for its Content-ID (text of a <c>data:image</c> address built by the application); when it returns null, or there is
    /// no resolver, the picture's address is taken out (with a resolver) or kept as it is (without). Never throws for any input.
    /// </summary>
    SanitizedHtml Sanitize(string? html, Func<string, string?>? resolveContentId, int maxInputLength) =>
        Sanitize(html, resolveContentId, maxInputLength, null);

    /// <summary>
    /// Cleans <paramref name="html"/>. An external picture (<c>img src</c> on the web) is replaced by what
    /// <paramref name="resolveExternalImage"/> returns for its address (the text of a <c>data:image</c> address the
    /// application made from a picture it fetched itself); null, or no resolver, takes the picture out and counts it as
    /// blocked. The addresses asked for are in <see cref="SanitizedHtml.ExternalImages"/> either way.
    /// </summary>
    SanitizedHtml Sanitize(string? html, Func<string, string?>? resolveContentId, int maxInputLength, Func<string, string?>? resolveExternalImage);
}
