using System.Reflection;

namespace Vinette.Api.Endpoints;

public sealed record HealthResponse(string Status, string Version);

public static class HealthEndpoints
{
    public static readonly string ProductVersion = ResolveVersion();

    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder api)
    {
        api.MapGet("/health", () => new HealthResponse("ok", ProductVersion))
            .AllowAnonymous()
            .WithName("GetHealth")
            .WithTags("System");
        return api;
    }

    private static string ResolveVersion()
    {
        var info = typeof(HealthEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        // Strip SourceLink build metadata ("1.2.3+commit").
        return info?.Split('+')[0] ?? "0.0.0";
    }
}
