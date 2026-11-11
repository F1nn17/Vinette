using System.Globalization;
using System.Text;

namespace Vinette.Core.Mail;

/// <summary>Cleans up the short text shown under a message's subject in the list.</summary>
public static class PreviewText
{
    /// <summary>The longest preview, in characters. The server may send more (or less): it is cut here, not trusted.</summary>
    public const int MaxLength = 200;

    /// <summary>
    /// One line of plain text: runs of white space (line breaks, tabs, non-breaking spaces) become one space, control
    /// characters and invisible formatting characters (zero-width, bidirectional overrides) are dropped, and the result is
    /// cut to <see cref="MaxLength"/> characters with an ellipsis. Returns an empty string for nothing.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = new StringBuilder(Math.Min(text.Length, MaxLength + 1));
        var pendingSpace = false;
        var length = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var element = (string)enumerator.Current;
            if (IsSpace(element))
            {
                pendingSpace = result.Length > 0;
                continue;
            }

            if (IsInvisible(element))
            {
                continue;
            }

            if (length + (pendingSpace ? 1 : 0) + 1 > MaxLength)
            {
                _ = result.Append('…');
                return result.ToString();
            }

            if (pendingSpace)
            {
                _ = result.Append(' ');
                length++;
                pendingSpace = false;
            }

            _ = result.Append(element);
            length++;
        }

        return result.ToString();
    }

    private static bool IsSpace(string element) =>
        element.Length == 1 && (char.IsWhiteSpace(element[0]) || element[0] == ' ');

    private static bool IsInvisible(string element)
    {
        var category = CharUnicodeInfo.GetUnicodeCategory(element, 0);
        return category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator or UnicodeCategory.OtherNotAssigned
            && element != "‍"; // zero width joiner holds emoji sequences together
    }
}
