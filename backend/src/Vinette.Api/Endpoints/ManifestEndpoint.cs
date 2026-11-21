using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;
using Vinette.Api.Configuration;

namespace Vinette.Api.Endpoints;

/// <summary>
/// The web app manifest (<c>/manifest.webmanifest</c>), made from the branding of the instance: the name, the accent as the theme color and
/// the logo as the icon, so that "install" and the home-screen shortcut carry the administrator's brand and not ours.
/// </summary>
public static class ManifestEndpoint
{
    /// <summary>The media type of a manifest.</summary>
    public const string ContentType = "application/manifest+json";

    /// <summary>Browsers show at most about twelve characters under a home-screen icon.</summary>
    public const int ShortNameLength = 12;

    private const string Background = "#F3F4F6";

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static IEndpointRouteBuilder MapManifest(this IEndpointRouteBuilder app)
    {
        app.MapGet("/manifest.webmanifest", (HttpContext http, IOptions<BrandingOptions> branding) =>
        {
            // Short cache: a changed name or accent reaches an installed app within the hour, and nobody asks on every page.
            http.Response.Headers.CacheControl = "public, max-age=3600";
            return Results.Text(Render(branding.Value), ContentType);
        })
            .AllowAnonymous()
            .ExcludeFromDescription();
        return app;
    }

    /// <summary>The manifest text for a branding.</summary>
    internal static string Render(BrandingOptions branding)
    {
        var name = branding.Name.Trim();
        var shortName = name.Length <= ShortNameLength ? name : name[..ShortNameLength].TrimEnd();
        var manifest = new Manifest(
            name,
            shortName,
            "/",
            "/",
            "standalone",
            Background,
            branding.Accent,
            Icons(branding));
        return JsonSerializer.Serialize(manifest, Json);
    }

    private static Icon[] Icons(BrandingOptions branding)
    {
        // The administrator's logo, if there is one: its own type, any size (a vector logo scales; a picture is taken as it is).
        if (!string.IsNullOrWhiteSpace(branding.LogoPath))
        {
            var type = ContentTypes.TryGetContentType(branding.LogoPath, out var found) ? found : null;
            return [new Icon("/branding/logo", "any", type, "any")];
        }

        return
        [
            new Icon("/icons/icon-192.png", "192x192", "image/png", "any"),
            new Icon("/icons/icon-512.png", "512x512", "image/png", "any"),
        ];
    }

    private sealed record Manifest(
        string Name,
        [property: JsonPropertyName("short_name")] string ShortName,
        string Id,
        [property: JsonPropertyName("start_url")] string StartUrl,
        string Display,
        [property: JsonPropertyName("background_color")] string BackgroundColor,
        [property: JsonPropertyName("theme_color")] string ThemeColor,
        Icon[] Icons);

    private sealed record Icon(string Src, string Sizes, string? Type, string Purpose);
}
