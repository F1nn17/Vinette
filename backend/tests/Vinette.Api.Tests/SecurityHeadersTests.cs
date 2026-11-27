using System.ComponentModel.DataAnnotations;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Vinette.Api.Security;

namespace Vinette.Api.Tests;

public sealed class SecurityHeadersTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static WebApplicationFactory<Program> Factory(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            foreach (var (key, value) in settings)
            {
                b.UseSetting(key, value);
            }
        });

    private static string[] Directives(string policy) => [.. policy.Split("; ")];

    [Fact]
    public void The_Policy_Lets_Scripts_Come_Only_From_The_Application_And_Allows_Nothing_That_Is_Not_Needed()
    {
        var directives = Directives(SecurityHeaders.ContentSecurityPolicy(new SecurityHeadersOptions()));

        Assert.Contains("default-src 'none'", directives);
        Assert.Contains("script-src 'self'", directives); // no 'unsafe-inline', no 'unsafe-eval'
        Assert.Contains("connect-src 'self'", directives);
        Assert.Contains("object-src 'none'", directives);
        Assert.Contains("frame-src 'none'", directives);
        Assert.Contains("frame-ancestors 'none'", directives);
        Assert.Contains("base-uri 'self'", directives);
        Assert.Contains("form-action 'self'", directives);
        Assert.DoesNotContain(directives, d => d.StartsWith("script-src", StringComparison.Ordinal) && d.Contains("unsafe", StringComparison.Ordinal));
        Assert.DoesNotContain(directives, d => d.Contains('*', StringComparison.Ordinal));
    }

    [Fact]
    public void Other_Places_To_Connect_To_Are_Added_To_The_Policy_And_Only_To_That_Rule()
    {
        var policy = SecurityHeaders.ContentSecurityPolicy(new SecurityHeadersOptions { ConnectSources = ["wss://live.example.com", "https://api.example.com:8443"] });

        Assert.Contains("connect-src 'self' wss://live.example.com https://api.example.com:8443", policy, StringComparison.Ordinal);
        Assert.Single(Directives(policy), d => d.Contains("live.example.com", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("wss://live.example.com", true)]
    [InlineData("https://example.com:8443", true)]
    [InlineData("http://localhost:5080", true)]
    [InlineData("https://example.com/path", false)]
    [InlineData("example.com", false)]
    [InlineData("ftp://example.com", false)]
    [InlineData("https://example.com; script-src *", false)]
    [InlineData("'unsafe-inline'", false)]
    public void Only_Origins_Can_Be_Added(string source, bool allowed)
    {
        Assert.Equal(allowed, SecurityHeadersOptions.IsConnectSource(source));
        var problems = new SecurityHeadersOptions { ConnectSources = [source] }.Validate(new ValidationContext(new object())).ToList();
        Assert.Equal(allowed, problems.Count == 0);
    }

    [Theory]
    [InlineData("365.00:00:00", 31536000)]
    [InlineData("1.00:00:00", 86400)]
    public void Hsts_Is_Max_Age_In_Seconds_Without_Subdomains_Or_Preload(string age, long seconds)
    {
        var header = SecurityHeaders.Hsts(new SecurityHeadersOptions { HstsMaxAge = TimeSpan.Parse(age, System.Globalization.CultureInfo.InvariantCulture) });

        Assert.Equal($"max-age={seconds}", header);
    }

    [Fact]
    public void Hsts_Can_Be_Turned_Off_And_A_Silly_Age_Is_Refused()
    {
        Assert.Null(SecurityHeaders.Hsts(new SecurityHeadersOptions { HstsMaxAge = TimeSpan.Zero }));
        Assert.NotEmpty(new SecurityHeadersOptions { HstsMaxAge = TimeSpan.FromDays(3650) }.Validate(new ValidationContext(new object())));
        Assert.NotEmpty(new SecurityHeadersOptions { HstsMaxAge = TimeSpan.FromDays(-1) }.Validate(new ValidationContext(new object())));
    }

    [Theory]
    [InlineData("/login")] // the application
    [InlineData("/api/v1/health")] // an API answer
    [InlineData("/api/v1/definitely-not-here")] // an error
    [InlineData("/favicon.svg")] // a static file, or the 404 where there is none in the tests
    public async Task Every_Kind_Of_Answer_Carries_The_Headers(string path)
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path, Token);

        var headers = response.Headers.Concat(response.Content.Headers).ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(SecurityHeaders.ContentSecurityPolicy(new SecurityHeadersOptions()), headers["Content-Security-Policy"]);
        Assert.Equal("nosniff", headers["X-Content-Type-Options"]);
        Assert.Equal("DENY", headers["X-Frame-Options"]);
        Assert.Equal("no-referrer", headers["Referrer-Policy"]);
        Assert.Contains("camera=()", headers["Permissions-Policy"], StringComparison.Ordinal);
        Assert.Contains("geolocation=()", headers["Permissions-Policy"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hsts_Is_Sent_Over_Https_And_Not_Over_Plain_Http()
    {
        using var factory = Factory();
        using var plain = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost") });
        using var secure = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

        var overHttp = await plain.GetAsync("/api/v1/health", Token);
        var overHttps = await secure.GetAsync("/api/v1/health", Token);

        Assert.False(overHttp.Headers.Contains("Strict-Transport-Security"));
        Assert.Equal("max-age=31536000", Assert.Single(overHttps.Headers.GetValues("Strict-Transport-Security")));
    }

    [Fact]
    public async Task Hsts_Is_Not_Sent_When_It_Is_Turned_Off()
    {
        using var factory = Factory(("Security:Headers:HstsMaxAge", "00:00:00"));
        using var secure = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

        var response = await secure.GetAsync("/api/v1/health", Token);

        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task A_Client_Cannot_Make_The_Server_Believe_It_Is_On_Https()
    {
        // X-Forwarded-Proto is only believed from the listed proxies; with none it is ignored.
        using var factory = Factory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost") });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health");
        request.Headers.Add("X-Forwarded-Proto", "https");

        var response = await client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Report_Only_Mode_Sends_The_Policy_Under_The_Other_Name_And_Blocks_Nothing()
    {
        using var factory = Factory(("Security:Headers:CspReportOnly", "true"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/health", Token);

        Assert.False(response.Headers.Contains("Content-Security-Policy"));
        Assert.Equal(SecurityHeaders.ContentSecurityPolicy(new SecurityHeadersOptions()), Assert.Single(response.Headers.GetValues("Content-Security-Policy-Report-Only")));
    }

    [Fact]
    public async Task The_Configured_Connect_Sources_Reach_The_Header()
    {
        using var factory = Factory(("Security:Headers:ConnectSources:0", "wss://live.example.com"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/health", Token);

        Assert.Contains("connect-src 'self' wss://live.example.com", Assert.Single(response.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_Bad_Connect_Source_Stops_The_Start()
    {
        using var factory = Factory(("Security:Headers:ConnectSources:0", "https://example.com; script-src *"));

        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }
}
