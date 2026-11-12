using System.Text;
using System.Text.RegularExpressions;

namespace Vinette.Core.Mail;

/// <summary>
/// How the name of a label is kept on the mail server. A label is an IMAP keyword on the message, and a keyword can only be a
/// simple ASCII word, is compared without regard to case, and is short (Dovecot: 50 characters). So:
/// <list type="bullet">
/// <item>A simple name (<c>Work</c>, <c>Bills-2026</c>) is the keyword itself, and other mail programs read it as it is.</item>
/// <item>Any other name (spaces, Cyrillic, ...) is <c>vl-</c> and the name in UTF-8 as lower-case base32 (letters and digits only,
/// so that the server's case folding cannot change it); only Vinette reads it back. Such a name takes about 1.6 times its
/// bytes, so it can be about 14 Cyrillic letters long.</item>
/// </list>
/// Names are compared without regard to case, like keywords.
/// </summary>
public static partial class LabelName
{
    /// <summary>The longest keyword the mail server keeps (Dovecot's default <c>mail_max_keyword_length</c>).</summary>
    public const int MaxKeywordLength = 50;

    private const string Prefix = "vl-";
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SimpleName();

    /// <summary>The name as it is saved: without the spaces around it.</summary>
    public static string Normalize(string name) => (name ?? string.Empty).Trim();

    /// <summary>Whether this can be the name of a label; otherwise says what is wrong, in words for a log or an API answer.</summary>
    public static bool IsValid(string? name, out string? problem)
    {
        var text = Normalize(name ?? string.Empty);
        if (text.Length == 0)
        {
            problem = "A label needs a name.";
        }
        else if (text.Any(char.IsControl) || text.Contains('/', StringComparison.Ordinal) || text.Contains('\\', StringComparison.Ordinal))
        {
            problem = "A label name must not contain control characters, / or \\.";
        }
        else if (ToKeyword(text).Length > MaxKeywordLength)
        {
            problem = "The label name is too long.";
        }
        else
        {
            problem = null;
        }

        return problem is null;
    }

    /// <summary>The keyword the label is kept as on the server. The name must be valid (<see cref="IsValid"/>).</summary>
    public static string ToKeyword(string name)
    {
        var text = Normalize(name);
        return SimpleName().IsMatch(text) && !text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            ? text
            : Prefix + Encode(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>The name of the label a keyword stands for; a keyword that is not one of ours is its own name.</summary>
    public static string FromKeyword(string keyword)
    {
        ArgumentNullException.ThrowIfNull(keyword);
        if (keyword.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && TryDecode(keyword[Prefix.Length..], out var bytes))
        {
            try
            {
                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (ArgumentException)
            {
                // not a name of ours after all: show it as it is
            }
        }

        return keyword;
    }

    /// <summary>Whether two names are the same label.</summary>
    public static bool Same(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Encode(ReadOnlySpan<byte> bytes)
    {
        var result = new StringBuilder((bytes.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;
        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                result.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            result.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return result.ToString();
    }

    private static bool TryDecode(string text, out byte[] bytes)
    {
        var result = new List<byte>(text.Length * 5 / 8);
        var buffer = 0;
        var bits = 0;
        foreach (var c in text.ToLowerInvariant())
        {
            var index = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (index < 0)
            {
                bytes = [];
                return false;
            }

            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                result.Add((byte)((buffer >> (bits - 8)) & 255));
                bits -= 8;
            }
        }

        bytes = [.. result];
        return bytes.Length > 0;
    }
}
