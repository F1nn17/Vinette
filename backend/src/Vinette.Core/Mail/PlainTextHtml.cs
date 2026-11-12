using System.Net;
using System.Text;

namespace Vinette.Core.Mail;

/// <summary>
/// Turns the plain text of a mail into HTML for the viewer: the text is escaped, line breaks and spaces are kept, web
/// addresses and e-mail addresses become links, and quoted lines (<c>&gt; …</c>) become nested quotations. The result is
/// still untrusted markup of the same kind as a mail's own HTML and goes through the sanitizer like it.
/// </summary>
public static class PlainTextHtml
{
    /// <summary>The longest text that is converted, in characters; the rest is cut off.</summary>
    public const int MaxLength = 1_000_000;

    private const int MaxQuoteDepth = 8;
    private const int MaxUrlLength = 2048;

    /// <summary>The HTML for <paramref name="text"/>; sets <paramref name="truncated"/> if the text was cut.</summary>
    public static string ToHtml(string? text, out bool truncated) => ToHtml(text, out truncated, MaxLength);

    /// <summary>The HTML for <paramref name="text"/>, at most <paramref name="maxLength"/> characters of it; sets <paramref name="truncated"/> if the text was cut.</summary>
    public static string ToHtml(string? text, out bool truncated, int maxLength)
    {
        truncated = false;
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var limit = Math.Clamp(maxLength, 1, MaxLength);
        if (text.Length > limit)
        {
            text = text[..limit];
            truncated = true;
        }

        var html = new StringBuilder(text.Length + text.Length / 4 + 64);
        html.Append("<div style=\"white-space:pre-wrap;word-wrap:break-word\">");
        var depth = 0;
        var first = true;
        foreach (var rawLine in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var (quoteDepth, content) = SplitQuote(rawLine);
            for (; depth < quoteDepth; depth++)
            {
                html.Append("<blockquote>");
                first = true; // a block starts a new line by itself
            }

            for (; depth > quoteDepth; depth--)
            {
                html.Append("</blockquote>");
                first = true; // nor does the end of a block need a line break after it
            }

            if (!first)
            {
                html.Append('\n');
            }

            first = false;
            AppendLinked(html, content);
        }

        for (; depth > 0; depth--)
        {
            html.Append("</blockquote>");
        }

        html.Append("</div>");
        return html.ToString();
    }

    private static (int Depth, string Content) SplitQuote(string line)
    {
        var depth = 0;
        var i = 0;
        while (i < line.Length && depth < MaxQuoteDepth)
        {
            var j = i;
            while (j < line.Length && line[j] == ' ')
            {
                j++;
            }

            if (j < line.Length && line[j] == '>')
            {
                depth++;
                i = j + 1;
                if (i < line.Length && line[i] == ' ')
                {
                    i++; // one space belongs to the marker
                }
            }
            else
            {
                break;
            }
        }

        return (depth, line[i..]);
    }

    /// <summary>Writes <paramref name="text"/> escaped, with the web and mail addresses in it as links.</summary>
    private static void AppendLinked(StringBuilder html, string text)
    {
        var position = 0;
        while (position < text.Length)
        {
            var start = NextAddress(text, position, out var isMail);
            if (start < 0)
            {
                Escape(html, text.AsSpan(position));
                return;
            }

            var end = isMail ? EndOfMail(text, start) : EndOfUrl(text, start);
            if (end <= start)
            {
                Escape(html, text.AsSpan(position, start - position + 1));
                position = start + 1;
                continue;
            }

            Escape(html, text.AsSpan(position, start - position));
            var address = text.Substring(start, end - start);
            var target = isMail ? "mailto:" + address : address.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + address : address;
            html.Append("<a href=\"").Append(WebUtility.HtmlEncode(target)).Append("\">");
            Escape(html, address);
            html.Append("</a>");
            position = end;
        }
    }

    private static int NextAddress(string text, int from, out bool isMail)
    {
        isMail = false;
        var best = -1;
        foreach (var prefix in new[] { "https://", "http://", "www." })
        {
            var at = text.IndexOf(prefix, from, StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && (best < 0 || at < best) && (at == 0 || !char.IsLetterOrDigit(text[at - 1])))
            {
                best = at;
            }
        }

        var arobase = text.IndexOf('@', from);
        if (arobase > 0)
        {
            var mailStart = arobase;
            while (mailStart > from && IsLocalPart(text[mailStart - 1]) && arobase - mailStart < 64)
            {
                mailStart--;
            }

            if (mailStart < arobase && (best < 0 || mailStart < best))
            {
                isMail = true;
                return mailStart;
            }
        }

        return best;
    }

    private static int EndOfUrl(string text, int start)
    {
        var end = start;
        while (end < text.Length && end - start < MaxUrlLength && !char.IsWhiteSpace(text[end]) && text[end] is not ('<' or '>' or '"') && !char.IsControl(text[end]))
        {
            end++;
        }

        // Punctuation that ends a sentence is not part of the address; a closing bracket is, if it was opened inside.
        while (end > start)
        {
            var last = text[end - 1];
            if (last is '.' or ',' or ';' or ':' or '!' or '?' or '\'' or '*')
            {
                end--;
            }
            else if (last is ')' or ']' or '}' && CountOf(text, start, end, last) > CountOf(text, start, end, Opener(last)))
            {
                end--;
            }
            else
            {
                break;
            }
        }

        var address = text.AsSpan(start, end - start);
        var hostStart = address.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? 8 : address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? 7 : 4;
        return address.Length > hostStart && char.IsLetterOrDigit(address[hostStart]) ? end : start;
    }

    private static int EndOfMail(string text, int start)
    {
        var at = text.IndexOf('@', start);
        var end = at + 1;
        var dots = 0;
        while (end < text.Length && end - at < 255 && (char.IsAsciiLetterOrDigit(text[end]) || text[end] is '-' or '.'))
        {
            if (text[end] == '.')
            {
                dots++;
            }

            end++;
        }

        while (end > at + 1 && text[end - 1] is '.' or '-')
        {
            if (text[end - 1] == '.')
            {
                dots--;
            }

            end--;
        }

        var domain = text.AsSpan(at + 1, end - at - 1);
        return dots >= 1 && domain.Length >= 4 && char.IsAsciiLetter(domain[^1]) ? end : start;
    }

    private static bool IsLocalPart(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '%' or '+' or '-';

    private static char Opener(char closer) => closer switch { ')' => '(', ']' => '[', _ => '{' };

    private static int CountOf(string text, int start, int end, char c)
    {
        var count = 0;
        for (var i = start; i < end; i++)
        {
            if (text[i] == c)
            {
                count++;
            }
        }

        return count;
    }

    private static void Escape(StringBuilder html, ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            switch (c)
            {
                case '&': html.Append("&amp;"); break;
                case '<': html.Append("&lt;"); break;
                case '>': html.Append("&gt;"); break;
                case '"': html.Append("&quot;"); break;
                case '\'': html.Append("&#39;"); break;
                default: html.Append(c); break;
            }
        }
    }
}
