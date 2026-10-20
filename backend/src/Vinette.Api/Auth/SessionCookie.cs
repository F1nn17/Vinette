using Vinette.Api.Configuration;

namespace Vinette.Api.Auth;

/// <summary>
/// The session cookie: an opaque id, nothing else. "__Host-" makes browsers require Secure and Path=/ and
/// refuse the cookie from sub-domains, which stops cookie injection from sibling hosts.
/// </summary>
public static class SessionCookie
{
    public const string Name = "__Host-vinette_session";

    /// <summary>The id from the request cookie, or null. Not validated: use <see cref="Sessions.SessionService"/>.</summary>
    public static string? Read(HttpRequest request) =>
        request.Cookies.TryGetValue(Name, out var value) ? value : null;

    /// <summary>Session cookie (gone when the browser closes), or persistent for "stay signed in".</summary>
    public static void Write(HttpResponse response, string sessionId, bool rememberMe, SessionLifetimeOptions options, TimeProvider time) =>
        response.Cookies.Append(Name, sessionId, Options(rememberMe ? time.GetUtcNow() + options.RememberMeLifetime : null));

    /// <summary>Deletes the cookie. Attributes must match those it was set with, otherwise browsers ignore the deletion.</summary>
    public static void Clear(HttpResponse response) => response.Cookies.Delete(Name, Options(null));

    private static CookieOptions Options(DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        IsEssential = true,
        Expires = expires,
    };
}
