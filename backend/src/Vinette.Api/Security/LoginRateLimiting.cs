using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Vinette.Api.Configuration;
using Vinette.Api.Errors;

namespace Vinette.Api.Security;

public static class LoginRateLimiting
{
    public const string PolicyName = "login";

    public static IServiceCollection AddVinetteLoginRateLimiting(this IServiceCollection services)
    {
        services.AddSingleton<LoginAttemptTracker>();
        services.AddHostedService<LoginAttemptCleanupService>();

        // Every sign-in request counts here, successful or not: it protects the mail server from a flood.
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(PolicyName, context =>
            {
                var ip = context.Connection.RemoteIpAddress?.ToString();
                if (ip is null)
                {
                    return RateLimitPartition.GetNoLimiter("unknown");
                }

                var settings = context.RequestServices.GetRequiredService<IOptions<LoginRateLimitOptions>>().Value;
                return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = settings.MaxAttemptsPerMinutePerIp,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
            });

            limiter.OnRejected = async (context, cancellationToken) =>
            {
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : TimeSpan.FromMinutes(1);
                await WriteTooManyRequestsAsync(context.HttpContext, retryAfter, cancellationToken);
            };
        });
        return services;
    }

    /// <summary>429 with Retry-After (whole seconds, rounded up) and a problem+json body.</summary>
    public static async Task WriteTooManyRequestsAsync(HttpContext context, TimeSpan retryAfter, CancellationToken cancellationToken = default)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var problems = context.RequestServices.GetRequiredService<IProblemDetailsService>();
        await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails =
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too many sign-in attempts. Try again later.",
                Extensions = { ["code"] = ErrorCodes.TooManyRequests },
            },
        });
    }
}

/// <summary>Periodically drops forgotten sign-in failure counters.</summary>
public sealed class LoginAttemptCleanupService(LoginAttemptTracker tracker) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                tracker.RemoveExpired();
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
