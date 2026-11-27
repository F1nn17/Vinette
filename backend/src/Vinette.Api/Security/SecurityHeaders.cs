using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.Extensions.Options;

namespace Vinette.Api.Security;

/// <summary>Bound to the "Security:Headers" section (VIN-121): the headers that tell the browser how far to trust the page.</summary>
public sealed class SecurityHeadersOptions : IValidatableObject
{
    public const string SectionName = "Security:Headers";

    /// <summary>
    /// How long browsers must remember that the site is only reachable over HTTPS (<c>Strict-Transport-Security</c>). Sent only on
    /// answers over HTTPS (behind a trusted reverse proxy, as the proxy says). Zero turns it off. Subdomains and the preload list are not
    /// part of it: that is a decision about the whole domain, not about this application.
    /// </summary>
    public TimeSpan HstsMaxAge { get; set; } = TimeSpan.FromDays(365);

    /// <summary>
    /// Send the policy as <c>Content-Security-Policy-Report-Only</c>: the browser reports what it would have blocked in its console and
    /// blocks nothing. A way to find out what an unusual set-up (another address for the live channel) needs; not a protection.
    /// </summary>
    public bool CspReportOnly { get; set; }

    /// <summary>
    /// Other origins the page may connect to besides its own (<c>connect-src</c>), for example the address of the live channel when a proxy
    /// serves it from another host: <c>wss://live.example.com</c>. Origins only: scheme://host[:port] with http, https, ws or wss.
    /// </summary>
    public string[] ConnectSources { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (HstsMaxAge < TimeSpan.Zero || HstsMaxAge > TimeSpan.FromDays(731))
        {
            yield return new ValidationResult("HstsMaxAge must be between zero and two years.", [nameof(HstsMaxAge)]);
        }

        foreach (var source in ConnectSources)
        {
            if (!IsConnectSource(source))
            {
                yield return new ValidationResult(
                    $"'{source}' is not an origin: use http://, https://, ws:// or wss:// with a host and no path.",
                    [nameof(ConnectSources)]);
            }
        }
    }

    internal static bool IsConnectSource(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" or "ws" or "wss"
        && string.Equals(value, uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)
        && !value.Contains(';', StringComparison.Ordinal)
        && !value.Contains(' ', StringComparison.Ordinal);
}

/// <summary>
/// Security headers on every answer of the application. The page is built so that it needs very little (everything is its own); the
/// policy says so, and the browser then refuses the rest: a script that was smuggled in has no place to run and no place to send data to.
/// </summary>
public static class SecurityHeaders
{
    /// <summary>
    /// The content policy. Scripts only from the application itself (no inline scripts, no eval). Styles allow inline attributes, which
    /// the interface uses for sizes and which the frame with a message inherits (the message is sanitized and has its own stricter policy
    /// in the frame). Pictures: the application's own (the proxy for pictures of messages included) and small data pictures. Connections
    /// only to itself (the live channel is the same address), plus what the administrator adds. Nothing may embed the page.
    /// </summary>
    public static string ContentSecurityPolicy(SecurityHeadersOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var connect = string.Join(' ', options.ConnectSources.Prepend("'self'"));
        return string.Join(
            "; ",
            "default-src 'none'",
            "script-src 'self'",
            "style-src 'self' 'unsafe-inline'",
            "img-src 'self' data:",
            "font-src 'self' data:",
            $"connect-src {connect}",
            "manifest-src 'self'",
            "frame-src 'none'",
            "object-src 'none'",
            "worker-src 'none'",
            "media-src 'none'",
            "base-uri 'self'",
            "form-action 'self'",
            "frame-ancestors 'none'");
    }

    /// <summary>Whatever the browser may be asked for by the page: nothing that identifies the user or the device.</summary>
    internal const string PermissionsPolicy =
        "accelerometer=(), autoplay=(), camera=(), display-capture=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), midi=(), payment=(), usb=(), interest-cohort=()";

    /// <summary>The value of <c>Strict-Transport-Security</c>, or null when it is off.</summary>
    internal static string? Hsts(SecurityHeadersOptions options)
    {
        var seconds = (long)options.HstsMaxAge.TotalSeconds;
        return seconds > 0 ? string.Create(CultureInfo.InvariantCulture, $"max-age={seconds}") : null;
    }

    /// <summary>Sets the headers on the response when it starts, unless the endpoint set its own (a download may need a stricter one).</summary>
    public static IApplicationBuilder UseVinetteSecurityHeaders(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var options = context.RequestServices.GetRequiredService<IOptions<SecurityHeadersOptions>>().Value;
                var headers = context.Response.Headers;
                headers.TryAdd(options.CspReportOnly ? "Content-Security-Policy-Report-Only" : "Content-Security-Policy", ContentSecurityPolicy(options));
                headers.TryAdd("X-Content-Type-Options", "nosniff");
                headers.TryAdd("X-Frame-Options", "DENY");
                headers.TryAdd("Referrer-Policy", "no-referrer");
                headers.TryAdd("Permissions-Policy", PermissionsPolicy);
                if (context.Request.IsHttps && Hsts(options) is { } hsts)
                {
                    headers.TryAdd("Strict-Transport-Security", hsts);
                }

                return Task.CompletedTask;
            });
            await next(context);
        });
    }
}
