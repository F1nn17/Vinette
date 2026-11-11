using System.Text;

namespace Vinette.Core.Mail;

/// <summary>Makes a file name taken from a mail safe to show and to offer for download.</summary>
public static class AttachmentFileName
{
    /// <summary>The longest name that is kept, in UTF-16 units; the extension is kept when the name is cut.</summary>
    public const int MaxLength = 150;

    // The names Windows will not create as files, whatever the extension.
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// A plain file name: the part after the last "/" or "\" (so "../../setup.exe" is "setup.exe"), without control
    /// characters, direction overrides and characters that are special in file names; no leading or trailing dots and
    /// spaces; at most <see cref="MaxLength"/> long. If nothing usable is left, <paramref name="fallback"/>.
    /// </summary>
    public static string Sanitize(string? name, string fallback)
    {
        ArgumentException.ThrowIfNullOrEmpty(fallback);
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        var cut = name.LastIndexOfAny(['/', '\\']);
        var plain = cut >= 0 ? name[(cut + 1)..] : name;

        var builder = new StringBuilder(plain.Length);
        foreach (var rune in plain.EnumerateRunes())
        {
            builder.Append(IsUnwanted(rune) ? '_' : rune.ToString());
        }

        var cleaned = builder.ToString().Trim(' ', '.', '_');
        if (cleaned.Length == 0)
        {
            return fallback;
        }

        if (cleaned.Length > MaxLength)
        {
            cleaned = Shorten(cleaned);
        }

        var stem = cleaned.Contains('.', StringComparison.Ordinal) ? cleaned[..cleaned.IndexOf('.', StringComparison.Ordinal)] : cleaned;
        return Reserved.Contains(stem.TrimEnd()) ? "_" + cleaned : cleaned;
    }

    private static bool IsUnwanted(Rune rune)
    {
        var value = rune.Value;
        return value < 0x20
            || value == 0x7F
            || (value is >= 0x80 and < 0xA0)
            || rune.Value is '<' or '>' or ':' or '"' or '|' or '?' or '*'
            || value is 0x061C or 0x200E or 0x200F or (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069) // direction marks and overrides
            || value is 0x200B or 0x200C or 0x200D or 0x2060 or 0xFEFF // zero-width characters
            || value == 0xFFFD
            || Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.OtherNotAssigned or System.Globalization.UnicodeCategory.Surrogate;
    }

    private static string Shorten(string name)
    {
        var dot = name.LastIndexOf('.');
        var extension = dot > 0 && name.Length - dot <= 16 ? name[dot..] : string.Empty;
        var room = MaxLength - extension.Length;
        var stem = name[..Math.Min(dot > 0 && extension.Length > 0 ? dot : name.Length, room)];
        if (stem.Length > 0 && char.IsHighSurrogate(stem[^1]))
        {
            stem = stem[..^1]; // never cut a character in two
        }

        return stem + extension;
    }
}
