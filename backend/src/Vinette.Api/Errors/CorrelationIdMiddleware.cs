using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Vinette.Api.Errors;

/// <summary>
/// Gives every request a correlation id: taken from X-Correlation-Id when it is well-formed,
/// otherwise generated. The id is echoed in the response, added to the logging scope and to ProblemDetails.
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";
    private const string ItemKey = "Vinette.CorrelationId";

    public async Task InvokeAsync(HttpContext context)
    {
        var id = context.Request.Headers[HeaderName].FirstOrDefault();
        if (id is null || !ValidId().IsMatch(id))
        {
            id = Guid.NewGuid().ToString("N");
        }

        context.Items[ItemKey] = id;
        // OnStarting: the exception handler clears response headers, so set it at the moment the response starts.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = id;
            return Task.CompletedTask;
        });
        Activity.Current?.SetTag("correlation.id", id);

        var scope = new Dictionary<string, object> { ["CorrelationId"] = id };
        if (context.Connection.RemoteIpAddress is { } clientIp)
        {
            scope["ClientIp"] = clientIp.ToString(); // the real client when trusted proxies are configured
        }

        using (logger.BeginScope(scope))
        {
            await next(context);
        }
    }

    public static string? Get(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var id) ? id as string : null;

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex ValidId();
}
