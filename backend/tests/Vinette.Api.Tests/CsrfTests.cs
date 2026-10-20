using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vinette.Api.Auth;
using Vinette.Api.Security;
using Vinette.Api.Sessions;

namespace Vinette.Api.Tests;

public sealed class CsrfTests : IDisposable
{
    private const string LogoutUrl = "/api/v1/auth/logout"; // anonymous and state-changing: a convenient probe

    private readonly WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        b.UseSetting("Security:TrustedOrigins:0", "https://mail.example.org"));

    public void Dispose() => factory.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private HttpClient CreateClient() =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string? token,
        string? csrfCookie,
        string? origin = CsrfClient.Origin,
        string? referer = null,
        string? sessionCookie = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, LogoutUrl);
        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        if (referer is not null)
        {
            request.Headers.TryAddWithoutValidation("Referer", referer);
        }

        if (token is not null)
        {
            request.Headers.Add(CsrfProtection.HeaderName, token);
        }

        var cookies = new List<string>();
        if (csrfCookie is not null)
        {
            cookies.Add(csrfCookie);
        }

        if (sessionCookie is not null)
        {
            cookies.Add(sessionCookie);
        }

        if (cookies.Count > 0)
        {
            request.Headers.Add("Cookie", string.Join("; ", cookies));
        }

        return await client.SendAsync(request, Token);
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token)).RootElement.GetProperty("code").GetString();

    // ---- The token endpoint ----

    [Fact]
    public async Task Token_Endpoint_Issues_A_Token_And_A_Hardened_Cookie()
    {
        var response = await CreateClient().GetAsync("/api/v1/csrf", Token);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(CsrfProtection.CookieName + "=", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token)).RootElement.GetProperty("token").GetString()));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_Existing_Cookie_Is_Reused_Instead_Of_Being_Replaced()
    {
        var client = CreateClient();
        var (_, cookie) = await CsrfClient.GetTokenAsync(client, null, Token);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/csrf");
        request.Headers.Add("Cookie", cookie);

        var again = await client.SendAsync(request, Token);

        Assert.False(again.Headers.Contains("Set-Cookie"));
    }

    // ---- State-changing requests ----

    [Fact]
    public async Task A_Valid_Token_And_Origin_Are_Accepted()
    {
        var client = CreateClient();
        var (token, cookie) = await CsrfClient.GetTokenAsync(client, null, Token);

        var response = await PostAsync(client, token, cookie);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Without_A_Token_The_Request_Is_Refused()
    {
        var client = CreateClient();
        var (_, cookie) = await CsrfClient.GetTokenAsync(client, null, Token);

        var response = await PostAsync(client, token: null, cookie);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("csrf_failed", await CodeAsync(response));
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Without_The_Cookie_The_Request_Is_Refused()
    {
        var client = CreateClient();
        var (token, _) = await CsrfClient.GetTokenAsync(client, null, Token);

        var response = await PostAsync(client, token, csrfCookie: null);

        Assert.Equal("csrf_failed", await CodeAsync(response));
    }

    [Fact]
    public async Task A_Token_From_Another_Cookie_Pair_Is_Refused()
    {
        var client = CreateClient();
        var (token, _) = await CsrfClient.GetTokenAsync(client, null, Token);
        var (_, otherCookie) = await CsrfClient.GetTokenAsync(client, null, Token);

        var response = await PostAsync(client, token, otherCookie);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_Tampered_Token_Is_Refused()
    {
        var client = CreateClient();
        var (token, cookie) = await CsrfClient.GetTokenAsync(client, null, Token);

        var response = await PostAsync(client, token[..^4] + "AAAA", cookie);

        Assert.Equal("csrf_failed", await CodeAsync(response));
    }

    [Fact]
    public async Task A_Token_Issued_Before_Sign_In_Is_Refused_After_Sign_In_So_Clients_Must_Refetch()
    {
        var client = CreateClient();
        var (anonymousToken, anonymousCookie) = await CsrfClient.GetTokenAsync(client, null, Token);
        var session = await factory.Services.GetRequiredService<SessionService>().CreateAsync("alice@vinette.test", "pw", false, null, Token);

        var stale = await PostAsync(client, anonymousToken, anonymousCookie, sessionCookie: $"{SessionCookie.Name}={session.Id}");
        var (freshToken, freshCookie) = await CsrfClient.GetTokenAsync(client, session.Id, Token);
        var fresh = await PostAsync(client, freshToken, freshCookie, sessionCookie: $"{SessionCookie.Name}={session.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, fresh.StatusCode);
    }

    [Fact]
    public async Task Safe_Methods_Need_Neither_Token_Nor_Origin()
    {
        var response = await CreateClient().GetAsync("/api/v1/health", Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- Origin ----

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://localhost:8080")] // same host, different port
    [InlineData("null")]
    [InlineData("not a url")]
    public async Task A_Foreign_Origin_Is_Refused_Even_With_A_Valid_Token(string origin)
    {
        var client = CreateClient();
        var (token, cookie) = await CsrfClient.GetTokenAsync(client, null, Token);

        var response = await PostAsync(client, token, cookie, origin);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("origin_not_allowed", await CodeAsync(response));
    }

    [Fact]
    public async Task Neither_Origin_Nor_Referer_Is_Refused()
    {
        var client = CreateClient();
        var (token, cookie) = await CsrfClient.GetTokenAsync(client, null, Token);

        var response = await PostAsync(client, token, cookie, origin: null);

        Assert.Equal("origin_not_allowed", await CodeAsync(response));
    }

    [Fact]
    public async Task The_Referer_Is_Used_When_There_Is_No_Origin()
    {
        var client = CreateClient();
        var (token, cookie) = await CsrfClient.GetTokenAsync(client, null, Token);

        var same = await PostAsync(client, token, cookie, origin: null, referer: "http://localhost/mail/inbox");
        var foreign = await PostAsync(client, token, cookie, origin: null, referer: "https://evil.example/page");

        Assert.Equal(HttpStatusCode.NoContent, same.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
    }

    [Fact]
    public async Task A_Configured_Trusted_Origin_Is_Accepted()
    {
        var client = CreateClient();
        var (token, cookie) = await CsrfClient.GetTokenAsync(client, null, Token);

        var response = await PostAsync(client, token, cookie, origin: "https://mail.example.org");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task The_Hub_Rejects_A_Foreign_Origin_But_Not_A_Missing_One()
    {
        var client = CreateClient();

        using var foreign = new HttpRequestMessage(HttpMethod.Get, "/hubs/mail");
        foreign.Headers.Add("Origin", "https://evil.example");
        using var none = new HttpRequestMessage(HttpMethod.Get, "/hubs/mail");

        var foreignResponse = await client.SendAsync(foreign, Token);
        var noneResponse = await client.SendAsync(none, Token);

        Assert.Equal(HttpStatusCode.Forbidden, foreignResponse.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, noneResponse.StatusCode); // reaches authentication: 401
    }

    // ---- Configuration ----

    [Theory]
    [InlineData("https://mail.example.org/")]
    [InlineData("https://mail.example.org/path")]
    [InlineData("mail.example.org")]
    [InlineData("ftp://mail.example.org")]
    public void Malformed_Trusted_Origins_Fail_Startup(string origin)
    {
        using var broken = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("Security:TrustedOrigins:0", origin));

        Assert.Throws<OptionsValidationException>(() => broken.CreateClient());
    }

    // ---- OriginPolicy in isolation ----

    private static HttpRequest Request(string host, string? origin = null, string? referer = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }

        if (referer is not null)
        {
            context.Request.Headers.Referer = referer;
        }

        return context.Request;
    }

    [Theory]
    [InlineData("mail.example.com", "https://mail.example.com", true)] // behind a TLS proxy the app sees http, scheme is not compared
    [InlineData("mail.example.com", "http://mail.example.com", true)]
    [InlineData("MAIL.example.com", "https://mail.EXAMPLE.com", true)]
    [InlineData("mail.example.com:8443", "https://mail.example.com:8443", true)]
    [InlineData("mail.example.com:8443", "https://mail.example.com", false)]
    [InlineData("mail.example.com", "https://mail.example.com:8443", false)]
    [InlineData("mail.example.com", "https://evil.example.com", false)]
    [InlineData("mail.example.com", "https://mail.example.com.evil.example", false)]
    [InlineData("mail.example.com", "https://evilmail.example.com", false)]
    public void Own_Origin_Is_Compared_By_Host_And_Port(string host, string origin, bool expected)
    {
        Assert.Equal(expected, OriginPolicy.IsAllowed(Request(host, origin), [], requireHeader: true));
    }

    [Fact]
    public void A_Missing_Header_Is_Allowed_Only_When_Not_Required()
    {
        Assert.False(OriginPolicy.IsAllowed(Request("mail.example.com"), [], requireHeader: true));
        Assert.True(OriginPolicy.IsAllowed(Request("mail.example.com"), [], requireHeader: false));
    }

    [Fact]
    public void A_Present_Foreign_Header_Is_Refused_Even_When_Not_Required()
    {
        Assert.False(OriginPolicy.IsAllowed(Request("mail.example.com", "https://evil.example"), [], requireHeader: false));
    }

    [Fact]
    public void Trusted_Origins_Are_Compared_Exactly()
    {
        string[] trusted = ["https://app.example.org"];

        Assert.True(OriginPolicy.IsAllowed(Request("mail.example.com", "https://app.example.org"), trusted, true));
        Assert.False(OriginPolicy.IsAllowed(Request("mail.example.com", "https://app.example.org.evil.example"), trusted, true));
        Assert.False(OriginPolicy.IsAllowed(Request("mail.example.com", "http://app.example.org"), trusted, true));
    }
}
