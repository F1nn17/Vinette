using System.ComponentModel.DataAnnotations;

namespace Vinette.Api.Configuration;

public sealed class BrandingOptions
{
    public const string SectionName = "Branding";

    [Required, MaxLength(64)]
    public string Name { get; set; } = "Vinette";

    /// <summary>Path to a logo file on disk. Empty means the built-in logo.</summary>
    public string? LogoPath { get; set; }

    /// <summary>Path to a favicon file on disk. Empty means the built-in favicon.</summary>
    public string? FaviconPath { get; set; }

    [Required, RegularExpression("^#[0-9A-Fa-f]{6}$", ErrorMessage = "Accent must be a color in #RRGGBB format.")]
    public string Accent { get; set; } = "#8E2043";

    [MaxLength(200)]
    public string? Tagline { get; set; }

    /// <summary>Show the "Powered by Vinette" credit.</summary>
    public bool PoweredBy { get; set; } = true;
}
