using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vinette.Api.Auth;
using Vinette.Api.Sessions;

namespace Vinette.Api.Tests;

public sealed class AuthorizationTests : IDisposable
{
    private const string Address = "alice@vinette.test";

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }

    private static readonly string[] ExpectedAnonymousEndpoints =
    [
        "* {*path:nonfile}", // the SPA fallback; it never serves /api, /hubs or /branding
        "GET /api/v1/health",
        "GET /branding/favicon",
        "GET /branding/logo",
        "GET /openapi/{documentName}.json", // Development only
        "POST /api/v1/auth/login",
        "POST /api/v1/auth/logout",
    ];

    private readonly ManualTimeProvider time = new();
    private readonly WebApplicationFactory<Program> factory;

    public AuthorizationTests()
    {
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(time);
            }));
    }

    public void Dispose() => factory.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private HttpClient CreateClient() =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private async Task<string> SignInAsync(string address = Address, bool rememberMe = false)
    {
        var session = await factory.Services.GetRequiredService<SessionService>()
            .CreateAsync(address, "pw", rememberMe, null, Token);
        return session.Id;
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string url, string? sessionCookie = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (sessionCookie is not null)
        {
            request.Headers.Add("Cookie", $"{SessionCookie.Name}={sessionCookie}");
        }

        return await client.SendAsync(request, Token);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token)).RootElement;

    // ---- /me ----

    [Fact]
    public async Task Me_Returns_The_Signed_In_Mailbox()
    {
        var id = await SignInAsync();

        var response = await GetAsync(CreateClient(), "/api/v1/me", id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Address, (await JsonAsync(response)).GetProperty("address").GetString());
    }

    [Fact]
    public async Task Me_Without_A_Session_Is_401_Problem_Json_Without_A_Redirect()
    {
        var response = await GetAsync(CreateClient(), "/api/v1/me");
        var problem = await JsonAsync(response);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("unauthorized", problem.GetProperty("code").GetString());
        Assert.True(response.Headers.Contains("X-Correlation-Id"));
        Assert.Null(response.Headers.Location);
        Assert.False(response.Headers.Contains("Set-Cookie")); // nothing to clear
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("0123456789012345678901234567890123456789012")] // well-formed shape, unknown session
    public async Task Me_With_An_Unknown_Session_Is_401_And_The_Cookie_Is_Cleared(string cookie)
    {
        var response = await GetAsync(CreateClient(), "/api/v1/me", cookie);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var cleared = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith($"{SessionCookie.Name}=;", cleared, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Me_After_The_Idle_Timeout_Is_401()
    {
        var id = await SignInAsync();
        time.Advance(TimeSpan.FromHours(3));

        var response = await GetAsync(CreateClient(), "/api/v1/me", id);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Requests_Keep_The_Session_Alive_Within_The_Idle_Timeout()
    {
        var id = await SignInAsync();
        var client = CreateClient();

        for (var i = 0; i < 4; i++)
        {
            time.Advance(TimeSpan.FromHours(1.5));
            Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/api/v1/me", id)).StatusCode);
        }
    }

    [Fact]
    public async Task Me_After_Logout_Is_401()
    {
        var id = await SignInAsync();
        var client = CreateClient();
        using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        logout.Headers.Add("Cookie", $"{SessionCookie.Name}={id}");
        await client.SendAsync(logout, Token);

        var response = await GetAsync(client, "/api/v1/me", id);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- Default policy ----

    [Theory]
    [InlineData("/api/v1/health")]
    [InlineData("/branding/logo")]
    [InlineData("/some/client/route")]
    public async Task Public_Endpoints_Do_Not_Require_A_Session(string url)
    {
        var response = await GetAsync(CreateClient(), url);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_Mail_Hub_Requires_A_Session()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/hubs/mail/negotiate?negotiateVersion=1");

        var response = await CreateClient().SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_Fallback_Policy_Requires_An_Authenticated_User()
    {
        var provider = factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        var fallback = await provider.GetFallbackPolicyAsync();

        Assert.NotNull(fallback);
        Assert.Contains(fallback.Requirements, r => r is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
    }

    /// <summary>
    /// The list of anonymous endpoints is closed: a new endpoint is protected by default, and opening one means
    /// adding it here on purpose (and reviewing why).
    /// </summary>
    [Fact]
    public void Only_The_Expected_Endpoints_Are_Anonymous()
    {
        var anonymous = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(e => $"{string.Join(",", e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])} {e.RoutePattern.RawText}")
            .Order()
            .ToList();

        Assert.Equal(ExpectedAnonymousEndpoints.Order(), anonymous);
    }
}
