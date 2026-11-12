using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Ganss.Xss;
using Vinette.Core.Mail;

namespace Vinette.Mail;

/// <summary>
/// Cleans the HTML of a mail with a white list: only the tags, attributes, URL schemes and CSS properties below survive.
/// This is the first of three layers; the next ones are the sandboxed frame without scripts and the frame's own CSP.
/// The rules are described in docs/architecture.md; the vectors the rules must hold against are in the tests.
/// </summary>
public sealed partial class MessageHtmlSanitizer : IMessageHtmlSanitizer
{
    private static readonly string[] Tags =
    [
        "a", "abbr", "acronym", "address", "article", "aside", "b", "bdi", "bdo", "big", "blockquote", "br", "caption", "center",
        "cite", "code", "col", "colgroup", "dd", "del", "dfn", "div", "dl", "dt", "em", "figcaption", "figure", "font",
        "footer", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "i", "img", "ins", "kbd", "li", "main", "mark", "nav",
        "ol", "p", "pre", "q", "s", "samp", "section", "small", "span", "strike", "strong", "style", "sub", "sup", "table",
        "tbody", "td", "tfoot", "th", "thead", "tr", "tt", "u", "ul", "var", "wbr",
    ];

    private static readonly string[] Attributes =
    [
        "align", "alt", "bgcolor", "border", "cellpadding", "cellspacing", "class", "color", "colspan", "dir", "face", "height",
        "href", "hspace", "id", "lang", "nowrap", "rowspan", "size", "src", "start", "style", "title", "valign", "vspace",
        "width", "span", "type", "target", "rel",
    ];

    private static readonly string[] CssProperties =
    [
        "background", "background-color", "background-position", "background-repeat", "background-size", "border", "border-bottom",
        "border-bottom-color", "border-bottom-left-radius", "border-bottom-right-radius", "border-bottom-style", "border-bottom-width",
        "border-collapse", "border-color", "border-left", "border-left-color", "border-left-style", "border-left-width", "border-radius",
        "border-right", "border-right-color", "border-right-style", "border-right-width", "border-spacing", "border-style", "border-top",
        "border-top-color", "border-top-left-radius", "border-top-right-radius", "border-top-style", "border-top-width", "border-width",
        "box-sizing", "clear", "color", "direction", "display", "float", "font", "font-family", "font-size", "font-style",
        "font-variant", "font-weight", "height", "letter-spacing", "line-height", "list-style", "list-style-type", "margin",
        "margin-bottom", "margin-left", "margin-right", "margin-top", "max-height", "max-width", "min-height", "min-width", "opacity",
        "overflow", "overflow-wrap", "padding", "padding-bottom", "padding-left", "padding-right", "padding-top", "table-layout",
        "text-align", "text-decoration", "text-indent", "text-overflow", "text-transform", "vertical-align", "visibility",
        "white-space", "width", "word-break", "word-spacing", "word-wrap",
    ];

    private static readonly string[] Schemes = ["http", "https", "mailto", "tel", "cid", "data"];

    // Elements whose attributes may hold a link that is followed when the user clicks.
    private static readonly HashSet<string> LinkTags = new(StringComparer.OrdinalIgnoreCase) { "a" };

    /// <summary>Whatever in a style could load or run something: such a declaration is dropped whole.</summary>
    [GeneratedRegex(@"url\s*\(|image-set\s*\(|expression\s*\(|javascript\s*:|vbscript\s*:|behavior\s*:|-moz-binding|@import|\\", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ActiveCss();

    [GeneratedRegex(@"^data:image/(png|jpeg|gif|webp);base64,([A-Za-z0-9+/=\s]*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DataImage();

    [GeneratedRegex(@"[^;{}]*(url\s*\(|image-set\s*\(|expression\s*\(|javascript\s*:|vbscript\s*:|behavior\s*:|-moz-binding|@import|\\)[^;{}]*;?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ActiveCssDeclaration();

    public SanitizedHtml Sanitize(string? html) => Sanitize(html, null);

    public SanitizedHtml Sanitize(string? html, Func<string, string?>? resolveContentId)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return SanitizedHtml.Empty;
        }

        var truncated = html.Length > IMessageHtmlSanitizer.MaxInputLength;
        var input = truncated ? html[..IMessageHtmlSanitizer.MaxInputLength] : html;
        if (TooDeep(input))
        {
            return new SanitizedHtml(string.Empty, 0, truncated, true);
        }

        var blocked = 0;
        var sanitizer = Build(() => blocked++, resolveContentId);
        var clean = sanitizer.Sanitize(input);
        clean = DropActiveStyleParts(clean, () => blocked++);
        return new SanitizedHtml(clean, blocked, truncated, false);
    }

    private static HtmlSanitizer Build(Action blockedExternal, Func<string, string?>? resolveContentId)
    {
        var sanitizer = new HtmlSanitizer(new HtmlSanitizerOptions
        {
            AllowedTags = new HashSet<string>(Tags, StringComparer.OrdinalIgnoreCase),
            AllowedAttributes = new HashSet<string>(Attributes, StringComparer.OrdinalIgnoreCase),
            AllowedCssProperties = new HashSet<string>(CssProperties, StringComparer.OrdinalIgnoreCase),
            AllowedSchemes = new HashSet<string>(Schemes, StringComparer.OrdinalIgnoreCase),
            AllowedAtRules = new HashSet<AngleSharp.Css.Dom.CssRuleType> { AngleSharp.Css.Dom.CssRuleType.Style, AngleSharp.Css.Dom.CssRuleType.Media },
            UriAttributes = new HashSet<string>(["href", "src", "cite", "background", "action", "formaction", "poster", "data", "srcset"], StringComparer.OrdinalIgnoreCase),
        })
        {
            KeepChildNodes = false,
            AllowDataAttributes = false,
            AllowCssCustomProperties = false,
        };

        sanitizer.FilterUrl += (_, e) =>
        {
            var tag = e.Tag.LocalName;
            var url = e.OriginalUrl.Trim();
            if (LinkTags.Contains(tag))
            {
                // Links: the web, mail and phone, and places inside the mail. Anything else (javascript:, data:, a path) goes.
                e.SanitizedUrl = IsAllowedLink(url) ? url : null;
                return;
            }

            if (string.Equals(tag, "img", StringComparison.OrdinalIgnoreCase))
            {
                if (url.StartsWith("cid:", StringComparison.OrdinalIgnoreCase) && url.Length > 4)
                {
                    // An inline picture of the mail itself: the application turns it into an embedded picture, or it goes.
                    e.SanitizedUrl = resolveContentId is null ? url : resolveContentId(url[4..]);
                    return;
                }

                if (IsAllowedDataImage(url))
                {
                    e.SanitizedUrl = url;
                    return;
                }

                if (IsExternal(url))
                {
                    blockedExternal();
                }

                e.SanitizedUrl = null;
                return;
            }

            // Backgrounds of tables and cells, quotation sources, anything else: nothing is fetched or followed.
            if (IsExternal(url))
            {
                blockedExternal();
            }

            e.SanitizedUrl = null;
        };

        // What the white list drops by itself is counted too when it would have loaded something from outside.
        sanitizer.RemovingAttribute += (_, e) =>
        {
            if (e.Attribute.Name is "background" or "srcset" or "poster" && HasExternalUrl(e.Attribute.Value))
            {
                blockedExternal();
            }
        };
        sanitizer.RemovingStyle += (_, e) =>
        {
            if (HasExternalUrl(e.Style.Value))
            {
                blockedExternal();
            }
        };
        sanitizer.RemovingAtRule += (_, e) =>
        {
            if (HasExternalUrl(e.Rule.CssText))
            {
                blockedExternal();
            }
        };

        sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is IElement element)
            {
                Harden(element, blockedExternal);
            }
        };

        return sanitizer;
    }

    private static void Harden(IElement element, Action blockedExternal)
    {
        if (element.LocalName == "a" && element.HasAttribute("href"))
        {
            element.SetAttribute("target", "_blank");
            element.SetAttribute("rel", "noopener noreferrer");
        }
        else
        {
            element.RemoveAttribute("target");
            element.RemoveAttribute("rel");
        }

        if (element.GetAttribute("style") is { } style)
        {
            var kept = DropDeclarations(style, blockedExternal);
            if (kept.Length == 0)
            {
                element.RemoveAttribute("style");
            }
            else
            {
                element.SetAttribute("style", kept);
            }
        }
    }

    /// <summary>The declarations of a <c>style</c> attribute without the ones that could load or run something.</summary>
    internal static string DropDeclarations(string style, Action blockedExternal)
    {
        var kept = new List<string>();
        foreach (var declaration in style.Split(';').Select(d => d.Trim()).Where(d => d.Length > 0))
        {
            if (!declaration.Contains(':', StringComparison.Ordinal))
            {
                continue;
            }

            if (ActiveCss().IsMatch(declaration))
            {
                if (HasExternalUrl(declaration))
                {
                    blockedExternal();
                }

                continue;
            }

            kept.Add(declaration);
        }

        return string.Join("; ", kept);
    }

    private static bool HasExternalUrl(string? text) =>
        text is not null && ExternalUrl().IsMatch(text);

    [GeneratedRegex(@"(https?:)?//[^\s)""']+|https?:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExternalUrl();

    private static string DropActiveStyleParts(string html, Action blockedExternal)
    {
        if (!html.Contains("<style", StringComparison.OrdinalIgnoreCase))
        {
            return html;
        }

        return StyleElement().Replace(html, match =>
        {
            var css = match.Groups["css"].Value;
            var cleaned = ActiveCssDeclaration().Replace(css, _ =>
            {
                blockedExternal();
                return string.Empty;
            });
            return $"{match.Groups["open"].Value}{cleaned}</style>";
        });
    }

    [GeneratedRegex(@"(?<open><style\b[^>]*>)(?<css>.*?)</style\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex StyleElement();

    private static bool IsAllowedLink(string url) =>
        url.StartsWith('#')
        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("tel:", StringComparison.OrdinalIgnoreCase);

    private static bool IsExternal(string url) =>
        url.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("//", StringComparison.Ordinal)
        || url.StartsWith("ftp:", StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowedDataImage(string url)
    {
        var match = DataImage().Match(url);
        if (!match.Success)
        {
            return false;
        }

        var base64 = match.Groups[2].Value;
        var approximateBytes = (long)base64.Length * 3 / 4;
        return approximateBytes <= IMessageHtmlSanitizer.MaxDataImageBytes;
    }

    /// <summary>
    /// A cheap estimate of how deep the elements are nested, so that hostile markup is turned away before a parser or a
    /// serializer is asked to walk it (a deep tree can exhaust the stack, which cannot be caught).
    /// </summary>
    private static bool TooDeep(string html)
    {
        var depth = 0;
        for (var i = 0; i < html.Length - 1; i++)
        {
            if (html[i] != '<')
            {
                continue;
            }

            var next = html[i + 1];
            if (next == '/')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (char.IsAsciiLetter(next) && !IsVoidOrSelfClosing(html, i))
            {
                if (++depth > IMessageHtmlSanitizer.MaxNesting)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsVoidOrSelfClosing(string html, int start)
    {
        var end = html.IndexOf('>', start);
        if (end < 0)
        {
            return true;
        }

        if (html[end - 1] == '/')
        {
            return true;
        }

        var nameEnd = start + 1;
        while (nameEnd < end && char.IsAsciiLetterOrDigit(html[nameEnd]))
        {
            nameEnd++;
        }

        return html.AsSpan(start + 1, nameEnd - start - 1) is var name
            && (name.Equals("br", StringComparison.OrdinalIgnoreCase)
                || name.Equals("img", StringComparison.OrdinalIgnoreCase)
                || name.Equals("hr", StringComparison.OrdinalIgnoreCase)
                || name.Equals("col", StringComparison.OrdinalIgnoreCase)
                || name.Equals("wbr", StringComparison.OrdinalIgnoreCase)
                || name.Equals("meta", StringComparison.OrdinalIgnoreCase)
                || name.Equals("link", StringComparison.OrdinalIgnoreCase)
                || name.Equals("input", StringComparison.OrdinalIgnoreCase));
    }
}
