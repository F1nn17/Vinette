namespace Vinette.Core.Settings;

/// <summary>How tight the message list is: with the text preview or without it (DESIGN.md §5).</summary>
public enum ListDensity
{
    /// <summary>The default: a row has the preview.</summary>
    Comfortable,

    /// <summary>A row without the preview.</summary>
    Compact,
}

/// <summary>
/// What a user chose in the interface and what does not belong in the mailbox itself: list density, language, colors of
/// labels. One document per mailbox. New settings are added as new properties with a default, so documents written by an
/// older version stay readable and an older version ignores what it does not know.
/// </summary>
public sealed record UserSettings
{
    /// <summary>The most labels with a color a user can have.</summary>
    public const int MaxLabelColors = 200;

    /// <summary>The most senders whose pictures are shown without asking.</summary>
    public const int MaxTrustedImageSenders = 500;

    /// <summary>The longest label name or color, in UTF-16 units.</summary>
    public const int MaxTextLength = 64;

    /// <summary>What a user without any saved settings gets.</summary>
    public static UserSettings Default { get; } = new();

    /// <summary>The list density.</summary>
    public ListDensity Density { get; init; } = ListDensity.Comfortable;

    /// <summary>The interface language ("ru", "en"); null follows the browser.</summary>
    public string? Language { get; init; }

    /// <summary>The color of each label by the label's name; the color is a key from the palette of the design, not CSS.</summary>
    public IReadOnlyDictionary<string, string> LabelColors { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// The addresses (lower case, exactly as written in From) whose pictures from the web are shown without asking, when
    /// the sender check of the message is "verified". Sorted, without repeats.
    /// </summary>
    public IReadOnlyList<string> TrustedImageSenders { get; init; } = [];

    /// <summary>Whether pictures of the sender with this address are shown without asking (the address is compared without regard to case).</summary>
    public bool TrustsImagesFrom(string? address) =>
        !string.IsNullOrWhiteSpace(address) && TrustedImageSenders.Contains(address.Trim().ToLowerInvariant(), StringComparer.Ordinal);

    /// <summary>These settings with the sender added to (<paramref name="trusted"/>) or removed from the senders whose pictures are shown.</summary>
    public UserSettings WithImageSender(string address, bool trusted)
    {
        var key = address.Trim().ToLowerInvariant();
        var senders = TrustedImageSenders.Where(a => a != key);
        return this with { TrustedImageSenders = [.. (trusted ? senders.Append(key) : senders).Order(StringComparer.Ordinal)] };
    }

    /// <summary>
    /// Whether the settings are within the limits the store accepts (sizes and counts, no empty or control-character
    /// names): the store refuses what is not, so a client cannot fill the disk.
    /// </summary>
    public bool IsValid(out string? problem)
    {
        problem = null;
        if (!Enum.IsDefined(Density))
        {
            problem = "Density is not a known value.";
        }
        else if (Language is { } language && (language.Length is 0 or > 16 || language.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
        {
            problem = "Language must be a short language tag such as ru or en-GB.";
        }
        else if (TrustedImageSenders.Count > MaxTrustedImageSenders)
        {
            problem = $"At most {MaxTrustedImageSenders} senders can be trusted with pictures.";
        }
        else if (TrustedImageSenders.Any(a => a.Length is 0 or > 254 || a.Any(char.IsControl) || a.Trim().Length == 0))
        {
            problem = "A trusted sender must be an address of 1 to 254 characters without control characters.";
        }
        else if (LabelColors.Count > MaxLabelColors)
        {
            problem = $"At most {MaxLabelColors} labels can have a color.";
        }
        else if (LabelColors.Any(p => !IsText(p.Key) || !IsText(p.Value)))
        {
            problem = $"Label names and colors must be text of 1 to {MaxTextLength} characters without control characters.";
        }

        return problem is null;
    }

    private static bool IsText(string text) =>
        text.Length is > 0 and <= MaxTextLength && !text.Any(char.IsControl) && text.Trim().Length > 0;
}

/// <summary>The settings were rejected: they are outside the limits (see <see cref="UserSettings.IsValid"/>).</summary>
public sealed class InvalidSettingsException(string message) : Exception(message);

/// <summary>Keeps the settings of the users: one <see cref="UserSettings"/> document per mailbox.</summary>
public interface IUserSettingsStore
{
    /// <summary>The settings of <paramref name="mailbox"/>; the defaults if nothing was saved (or what was saved cannot be read).</summary>
    Task<UserSettings> GetAsync(string mailbox, CancellationToken cancellationToken);

    /// <summary>
    /// Changes the settings atomically: reads the current ones, applies <paramref name="change"/>, saves the result and
    /// returns it. Two changes at once never lose each other.
    /// </summary>
    /// <exception cref="InvalidSettingsException">The result is outside the limits; nothing is saved.</exception>
    Task<UserSettings> UpdateAsync(string mailbox, Func<UserSettings, UserSettings> change, CancellationToken cancellationToken);

    /// <summary>Removes everything kept for <paramref name="mailbox"/>.</summary>
    Task DeleteAsync(string mailbox, CancellationToken cancellationToken);
}
