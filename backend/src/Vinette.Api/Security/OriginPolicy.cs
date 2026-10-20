namespace Vinette.Api.Security;

/// <summary>
/// Decides whether a request comes from a page of this application. Browsers send Origin on every state-changing
/// request (Referer is the fallback); a mismatch means another site is driving the user's browser.
/// </summary>
public static class OriginPolicy
{
    /// <param name="requireHeader">
    /// True for state-changing requests: no Origin and no Referer is rejected. False where browsers legitimately omit
    /// both (same-origin GET): then only a present, mismatching header is rejected.
    /// </param>
    public static bool IsAllowed(HttpRequest request, IReadOnlyCollection<string> trustedOrigins, bool requireHeader)
    {
        var origin = request.Headers.Origin.ToString();
        if (origin.Length == 0)
        {
            origin = OriginOf(request.Headers.Referer.ToString()) ?? string.Empty;
        }

        if (origin.Length == 0)
        {
            return !requireHeader;
        }

        // "null" (sandboxed frames, some privacy modes) never matches anything.
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        return IsOwnOrigin(request, uri)
            || trustedOrigins.Any(trusted => string.Equals(trusted.TrimEnd('/'), uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The request's own address, compared by host and port only: behind a TLS-terminating proxy the application sees
    /// http while the browser says https, so the scheme is deliberately not compared.
    /// </summary>
    private static bool IsOwnOrigin(HttpRequest request, Uri origin)
    {
        if (!string.Equals(request.Host.Host, origin.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return request.Host.Port is { } port ? port == origin.Port : origin.IsDefaultPort;
    }

    private static string? OriginOf(string referer) =>
        Uri.TryCreate(referer, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;
}
