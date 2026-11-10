using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using Vinette.Api.Configuration;
using Vinette.Api.Errors;
using Vinette.Mail;

namespace Vinette.Api.Endpoints;

/// <summary>
/// <c>GET /metrics</c> in the Prometheus text format, protected by a bearer token (<c>Metrics:BearerToken</c>). The endpoint
/// exists only when <c>Metrics:Enabled</c> is true. It is not an API endpoint: it sits in the pipeline before routing,
/// so the "everything needs a session" policy does not apply and the token is the only gate.
/// </summary>
public static class MetricsEndpoint
{
    public const string Path = "/metrics";

    /// <summary>Registers the exporter and the meters when metrics are switched on in the configuration.</summary>
    public static IServiceCollection AddVinetteMetrics(this IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetSection(MetricsOptions.SectionName).GetValue<bool>(nameof(MetricsOptions.Enabled)))
        {
            return services;
        }

        _ = services.AddOpenTelemetry().WithMetrics(metrics => metrics
            .AddMeter(MailMetrics.MeterName)
            .AddPrometheusExporter());
        return services;
    }

    /// <summary>Serves <c>/metrics</c> for a caller with the right token; a no-op when metrics are off.</summary>
    public static IApplicationBuilder UseVinetteMetrics(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetRequiredService<IOptions<MetricsOptions>>().Value;
        if (!options.Enabled)
        {
            return app;
        }

        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(options.BearerToken!));
        _ = app.Use(async (context, next) =>
        {
            if (context.Request.Path != Path)
            {
                await next(context);
                return;
            }

            if (!IsAuthorized(context.Request, expected))
            {
                context.Response.Headers.WWWAuthenticate = "Bearer";
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.RequestServices.GetRequiredService<IProblemDetailsService>().TryWriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context,
                    ProblemDetails = { Status = StatusCodes.Status401Unauthorized, Title = "A bearer token is required.", Extensions = { ["code"] = ErrorCodes.Unauthorized } },
                });
                return;
            }

            await next(context);
        });
        _ = app.UseOpenTelemetryPrometheusScrapingEndpoint(Path);
        return app;
    }

    // Compares hashes, so the comparison takes the same time whatever the token looks like.
    private static bool IsAuthorized(HttpRequest request, byte[] expectedHash)
    {
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(header[prefix.Length..].Trim()));
        return CryptographicOperations.FixedTimeEquals(actual, expectedHash);
    }
}
