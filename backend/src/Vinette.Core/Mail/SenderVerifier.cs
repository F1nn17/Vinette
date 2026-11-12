using System.Text;
using System.Text.RegularExpressions;

namespace Vinette.Core.Mail;

/// <summary>What the receiving mail server's checks say about who sent a message.</summary>
public enum SenderVerdict
{
    /// <summary>Nothing reliable to say: no result from a server we trust, or an inconclusive one.</summary>
    Unknown,

    /// <summary>The domain in the From line passed the checks (DKIM, SPF, DMARC) of our own mail server.</summary>
    Verified,

    /// <summary>The checks failed: the sender claims to be somebody the domain's rules say they are not.</summary>
    Suspicious,
}

/// <summary>The outcome of the sender check.</summary>
/// <param name="Verdict">The verdict.</param>
/// <param name="Domain">The domain of the From address the verdict is about; null if the mail has none.</param>
public sealed record SenderCheck(SenderVerdict Verdict, string? Domain)
{
    /// <summary>No header, no result.</summary>
    public static SenderCheck Unknown(string? domain = null) => new(SenderVerdict.Unknown, domain);
}

/// <summary>
/// Turns the <c>Authentication-Results</c> headers (RFC 8601) of a message into a verdict. The header is only as good as
/// whoever wrote it, and a sender can write one too. So: a message counts as <see cref="SenderVerdict.Verified"/> only on
/// the word of the <b>topmost</b> header, and only if it names one of our own trusted servers (the receiving server puts its
/// header on top and removes impostors of its name); a bad result is believed from any header, because a forged
/// "fail" can only hurt the message that carries it.
/// </summary>
public sealed partial class SenderVerifier(IEnumerable<string> trustedAuthservIds)
{
    private readonly HashSet<string> trusted = new(
        (trustedAuthservIds ?? throw new ArgumentNullException(nameof(trustedAuthservIds))).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>One method's result in a header: <c>dkim=pass header.d=example.org</c>.</summary>
    internal sealed record MethodResult(string Method, string Result, IReadOnlyDictionary<string, string> Properties, string Comment);

    /// <summary>One header taken apart.</summary>
    internal sealed record ParsedHeader(string AuthservId, IReadOnlyList<MethodResult> Results);

    /// <summary>The verdict for a message with these headers (top first) and this From address.</summary>
    public SenderCheck Check(IReadOnlyList<string> authenticationResults, string? fromAddress)
    {
        ArgumentNullException.ThrowIfNull(authenticationResults);
        var domain = DomainOf(fromAddress);
        var headers = authenticationResults.Select(Parse).OfType<ParsedHeader>().ToList();
        if (headers.Count == 0)
        {
            return SenderCheck.Unknown(domain);
        }

        if (headers.Any(IsFailure))
        {
            return new SenderCheck(SenderVerdict.Suspicious, domain);
        }

        var top = headers[0];
        return trusted.Contains(top.AuthservId) && domain is not null && IsPass(top, domain)
            ? new SenderCheck(SenderVerdict.Verified, domain)
            : SenderCheck.Unknown(domain);
    }

    private static MethodResult? Find(ParsedHeader header, string method) =>
        header.Results.FirstOrDefault(r => r.Method.Equals(method, StringComparison.OrdinalIgnoreCase));

    private static bool IsFailure(ParsedHeader header)
    {
        var dmarc = Find(header, "dmarc");
        if (dmarc is not null)
        {
            // dmarc=fail on a domain whose policy is "none" is how mailing lists look: not a reason for an alarm.
            return Is(dmarc, "fail") && !PolicyIsNone(dmarc);
        }

        var spf = Find(header, "spf");
        var dkim = Find(header, "dkim");
        return (Is(spf, "fail") || Is(dkim, "fail")) && !Is(spf, "pass") && !Is(dkim, "pass");
    }

    private static bool IsPass(ParsedHeader header, string domain)
    {
        var dmarc = Find(header, "dmarc");
        if (dmarc is not null)
        {
            var from = dmarc.Properties.GetValueOrDefault("header.from");
            return Is(dmarc, "pass") && (from is null || Aligned(from, domain));
        }

        var dkim = Find(header, "dkim");
        if (Is(dkim, "pass") && dkim!.Properties.GetValueOrDefault("header.d") is { } signer && Aligned(signer, domain))
        {
            return true;
        }

        var spf = Find(header, "spf");
        return Is(spf, "pass") && DomainOf(spf!.Properties.GetValueOrDefault("smtp.mailfrom")) is { } envelope && Aligned(envelope, domain);
    }

    private static bool Is(MethodResult? result, string value) =>
        result is not null && result.Result.Equals(value, StringComparison.OrdinalIgnoreCase);

    private static bool PolicyIsNone(MethodResult dmarc) =>
        Policy().Match(dmarc.Comment) is { Success: true } m && m.Groups[1].Value.Equals("none", StringComparison.OrdinalIgnoreCase);

    /// <summary>Relaxed alignment: the same domain, or one is a subdomain of the other.</summary>
    private static bool Aligned(string a, string b)
    {
        a = a.Trim().TrimEnd('.').ToLowerInvariant();
        b = b.Trim().TrimEnd('.').ToLowerInvariant();
        return a.Length > 0 && b.Length > 0
            && (a == b || a.EndsWith("." + b, StringComparison.Ordinal) || b.EndsWith("." + a, StringComparison.Ordinal));
    }

    private static string? DomainOf(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var at = address.LastIndexOf('@');
        var domain = (at >= 0 ? address[(at + 1)..] : address).Trim().Trim('>').TrimEnd('.').ToLowerInvariant();
        return domain.Length is > 0 and <= 255 && !domain.Contains(' ', StringComparison.Ordinal) ? domain : null;
    }

    /// <summary>Takes one header apart; null if it has no authserv-id (nothing to trust or to read).</summary>
    internal static ParsedHeader? Parse(string header)
    {
        if (string.IsNullOrWhiteSpace(header) || header.Length > 16 * 1024)
        {
            return null;
        }

        var parts = SplitTopLevel(header);
        var id = parts[0].Trim();
        var space = id.IndexOfAny([' ', '\t']);
        if (space > 0)
        {
            id = id[..space]; // "mail.example.org 1": the version follows the id
        }

        id = id.Trim('"');
        if (id.Length == 0 || id.Contains('=', StringComparison.Ordinal))
        {
            return null;
        }

        var results = new List<MethodResult>();
        foreach (var part in parts.Skip(1))
        {
            var item = ParseResult(part);
            if (item is not null)
            {
                results.Add(item);
            }
        }

        return new ParsedHeader(id, results);
    }

    private static MethodResult? ParseResult(string text)
    {
        var comment = new StringBuilder();
        var tokens = Tokenize(text, comment);
        if (tokens.Count == 0)
        {
            return null;
        }

        var first = tokens[0];
        var equals = first.IndexOf('=', StringComparison.Ordinal);
        if (equals <= 0 || equals == first.Length - 1)
        {
            return null; // "none", or something that is not method=result
        }

        var method = first[..equals];
        var slash = method.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0)
        {
            method = method[..slash]; // dkim/1
        }

        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in tokens.Skip(1))
        {
            var eq = token.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && eq < token.Length - 1 && token[..eq].Contains('.', StringComparison.Ordinal))
            {
                properties.TryAdd(token[..eq], token[(eq + 1)..].Trim('"'));
            }
        }

        return new MethodResult(method, first[(equals + 1)..], properties, comment.ToString());
    }

    /// <summary>Splits at the semicolons that are not inside a quoted string or a comment in parentheses.</summary>
    private static List<string> SplitTopLevel(string text)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && (quoted || depth > 0))
            {
                current.Append(c).Append(text[++i]);
                continue;
            }

            if (c == '"' && depth == 0)
            {
                quoted = !quoted;
            }
            else if (!quoted && c == '(')
            {
                depth++;
            }
            else if (!quoted && c == ')' && depth > 0)
            {
                depth--;
            }
            else if (c == ';' && !quoted && depth == 0)
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        parts.Add(current.ToString());
        return parts;
    }

    /// <summary>The words of one result; text in parentheses is collected into <paramref name="comments"/> and not returned as words.</summary>
    private static List<string> Tokenize(string text, StringBuilder comments)
    {
        var tokens = new List<string>();
        var word = new StringBuilder();
        var depth = 0;
        var quoted = false;

        void Flush()
        {
            if (word.Length > 0)
            {
                tokens.Add(word.ToString());
                word.Clear();
            }
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (depth > 0)
            {
                if (c == '\\' && i + 1 < text.Length)
                {
                    i++;
                }
                else if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        comments.Append(' ');
                    }
                }
                else
                {
                    comments.Append(c);
                }

                continue;
            }

            if (quoted)
            {
                if (c == '\\' && i + 1 < text.Length)
                {
                    word.Append(text[++i]);
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    word.Append(c);
                }

                continue;
            }

            if (c == '(')
            {
                depth = 1;
                Flush();
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                Flush();
            }
            else
            {
                word.Append(c);
            }
        }

        Flush();
        return tokens;
    }

    [GeneratedRegex(@"\b(?:p|policy)\s*=\s*([a-z]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Policy();
}
