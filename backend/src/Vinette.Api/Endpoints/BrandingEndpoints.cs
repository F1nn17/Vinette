using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;
using Vinette.Api.Configuration;

namespace Vinette.Api.Endpoints;

public static class BrandingEndpoints
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    /// <summary>Serves administrator-provided logo and favicon files.</summary>
    public static IEndpointRouteBuilder MapBranding(this IEndpointRouteBuilder app)
    {
        app.MapGet("/branding/logo", (IOptions<BrandingOptions> o) => ServeFile(o.Value.LogoPath))
            .ExcludeFromDescription();
        app.MapGet("/branding/favicon", (IOptions<BrandingOptions> o) => ServeFile(o.Value.FaviconPath))
            .ExcludeFromDescription();
        return app;
    }

    private static IResult ServeFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Results.NotFound();
        }

        if (!ContentTypes.TryGetContentType(path, out var contentType))
        {
            contentType = "application/octet-stream";
        }

        return Results.File(Path.GetFullPath(path), contentType);
    }
}
