using System.Globalization;
using Microsoft.Extensions.Options;

namespace Vinette.Api.Configuration;

/// <summary>
/// The accent is the color of buttons with white text and of links on white (DESIGN.md, "Цвет"), so it must be readable against white:
/// WCAG AA asks for a contrast of 4.5 : 1 for text of this size.
/// </summary>
public static class AccentContrast
{
    /// <summary>The least contrast of the accent with white (WCAG 2.2 AA, normal text).</summary>
    public const double Minimum = 4.5;

    /// <summary>The contrast ratio of a #RRGGBB color with white, from 1 (the same) to 21 (black).</summary>
    public static double WithWhite(string hex)
    {
        var (r, g, b) = Parse(hex);
        return 1.05 / (Luminance(r, g, b) + 0.05);
    }

    /// <summary>
    /// The color itself if it is dark enough; otherwise the same color made darker, step by step, until it is (the hue stays, so a light
    /// blue becomes a deeper blue and not gray). The adjusted color has at least <see cref="Minimum"/>.
    /// </summary>
    public static string Ensure(string hex)
    {
        if (WithWhite(hex) >= Minimum)
        {
            return hex;
        }

        var (r, g, b) = Parse(hex);
        for (var factor = 0.97; factor > 0; factor -= 0.01)
        {
            var darker = Format(Scale(r, factor), Scale(g, factor), Scale(b, factor));
            if (WithWhite(darker) >= Minimum)
            {
                return darker;
            }
        }

        return "#000000";
    }

    private static int Scale(int channel, double factor) => (int)Math.Floor(channel * factor);

    private static string Format(int r, int g, int b) => string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{g:X2}{b:X2}");

    private static (int R, int G, int B) Parse(string hex)
    {
        var n = int.Parse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return ((n >> 16) & 0xff, (n >> 8) & 0xff, n & 0xff);
    }

    private static double Luminance(int r, int g, int b) => (0.2126 * Linear(r)) + (0.7152 * Linear(g)) + (0.0722 * Linear(b));

    private static double Linear(int channel)
    {
        var c = channel / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}

/// <summary>Makes a too light accent darker when the options are made (at start), and says so in the log.</summary>
public sealed partial class AccentContrastPostConfigure(ILogger<AccentContrastPostConfigure> logger) : IPostConfigureOptions<BrandingOptions>
{
    public void PostConfigure(string? name, BrandingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var adjusted = AccentContrast.Ensure(options.Accent);
        if (string.Equals(adjusted, options.Accent, StringComparison.Ordinal))
        {
            return;
        }

        LogDarkened(logger, options.Accent, AccentContrast.WithWhite(options.Accent), AccentContrast.Minimum, adjusted);
        options.Accent = adjusted;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Branding:Accent {Accent} is too light for white text (contrast {Contrast:F2}, at least {Minimum} is needed); {Adjusted} is used instead. Choose a darker accent to remove this warning.")]
    private static partial void LogDarkened(ILogger logger, string accent, double contrast, double minimum, string adjusted);
}
