using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Vinette.Api.Configuration;

namespace Vinette.Api.Spa;

/// <summary>
/// Serves index.html with branding injected by the server: title, favicon and a JSON settings block,
/// so the SPA needs no extra request before the first render.
/// </summary>
public static partial class SpaIndex
{
    public const string ConfigElementId = "vinette-config";
    public const string HeadMarker = "<!--vinette-head-->";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapSpa(this IEndpointRouteBuilder app)
    {
        app.MapFallback(async (HttpContext ctx, IWebHostEnvironment env, IOptions<BrandingOptions> branding) =>
        {
            var path = ctx.Request.Path;
            if (path.StartsWithSegments("/api") || path.StartsWithSegments("/hubs") || path.StartsWithSegments("/branding"))
            {
                return Results.NotFound();
            }

            var file = env.WebRootFileProvider.GetFileInfo("index.html");
            if (!file.Exists)
            {
                return Results.NotFound();
            }

            var html = await ReadAsync(file, ctx.RequestAborted);
            ctx.Response.Headers.CacheControl = "no-cache";
            return Results.Content(Render(html, branding.Value), "text/html; charset=utf-8");
        }).ExcludeFromDescription();
        return app;
    }

    internal static string Render(string html, BrandingOptions b)
    {
        var logoUrl = string.IsNullOrWhiteSpace(b.LogoPath) ? "/logo.svg" : "/branding/logo";
        var faviconUrl = string.IsNullOrWhiteSpace(b.FaviconPath) ? "/favicon.svg" : "/branding/favicon";

        var settings = JsonSerializer.Serialize(
            new { branding = new { b.Name, b.Tagline, b.Accent, b.PoweredBy, logoUrl, faviconUrl } },
            Json).Replace("<", "\\u003c", StringComparison.Ordinal);

        var head =
            $"""<link rel="icon" href="{faviconUrl}" /><meta name="theme-color" content="{b.Accent}" /><script id="{ConfigElementId}" type="application/json">{settings}</script>""";

        html = TitleTag().Replace(html, $"<title>{WebUtility.HtmlEncode(b.Name)}</title>", 1);
        html = IconLink().Replace(html, string.Empty);
        return html.Replace(HeadMarker, head, StringComparison.Ordinal);
    }

    private static async Task<string> ReadAsync(IFileInfo file, CancellationToken ct)
    {
        await using var stream = file.CreateReadStream();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }

    [GeneratedRegex("<title>.*?</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TitleTag();

    [GeneratedRegex("""<link[^>]*rel="icon"[^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex IconLink();
}
