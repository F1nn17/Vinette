using System.Net.Http.Json;
using System.Text.Json;
using Vinette.Api.Auth;
using Vinette.Api.Security;

namespace Vinette.Api.Tests;

/// <summary>
/// Sends requests the way the SPA does: Origin of the application, a fresh anti-forgery token (bound to the signed-in
/// user when a session cookie is given) in the header and the matching cookie.
/// </summary>
internal static class CsrfClient
{
    /// <summary>The address of the test server, which is also the origin of its pages.</summary>
    public const string Origin = "http://localhost";

    public static async Task<(string Token, string Cookie)> GetTokenAsync(HttpClient client, string? sessionId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/csrf");
        if (sessionId is not null)
        {
            request.Headers.Add("Cookie", $"{SessionCookie.Name}={sessionId}");
        }

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var token = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)).RootElement.GetProperty("token").GetString()!;
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(CsrfProtection.CookieName + "=", StringComparison.Ordinal)).Split(';')[0];
        return (token, cookie);
    }

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string url,
        CancellationToken cancellationToken,
        HttpContent? content = null,
        string? sessionId = null)
    {
        var (token, csrfCookie) = await GetTokenAsync(client, sessionId, cancellationToken);
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Add("Origin", Origin);
        request.Headers.Add(CsrfProtection.HeaderName, token);
        request.Headers.Add("Cookie", sessionId is null ? csrfCookie : $"{csrfCookie}; {SessionCookie.Name}={sessionId}");
        return await client.SendAsync(request, cancellationToken);
    }

    public static Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string url, object body, CancellationToken cancellationToken, string? sessionId = null) =>
        SendAsync(client, HttpMethod.Post, url, cancellationToken, JsonContent.Create(body), sessionId);
}
