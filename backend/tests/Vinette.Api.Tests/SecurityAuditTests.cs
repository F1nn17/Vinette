using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vinette.Api.Security;

namespace Vinette.Api.Tests;

/// <summary>
/// What the security audit (VIN-122) found or checked and wants to stay true: a closed list instead of a promise. The other points of the
/// checklist have their tests where the code is (cookie: <c>AuthTests</c>, anonymous endpoints: <c>AuthorizationTests</c>, rate limit:
/// <c>LoginRateLimitTests</c>, forwarded headers: <c>ForwardedHeadersTests</c>, sanitizer: its XSS vectors, pictures: <c>ExternalImageFetcherTests</c>).
/// </summary>
public sealed class SecurityAuditTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Every route that changes something is behind the anti-forgery check: no endpoint can be added that forgets it.</summary>
    [Fact]
    public async Task Every_Route_That_Changes_State_Refuses_A_Request_Without_The_Security_Token()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Select(method => (method, path: e.RoutePattern.RawText ?? string.Empty)))
            .Where(r => r.method is not ("GET" or "HEAD" or "OPTIONS" or "TRACE") && !r.path.StartsWith("/hubs", StringComparison.Ordinal))
            .Distinct()
            .ToList();

        Assert.True(routes.Count >= 15, $"only {routes.Count} routes that change state were found: is the list being read right?");
        var open = new List<string>();
        foreach (var (method, path) in routes)
        {
            var concrete = System.Text.RegularExpressions.Regex.Replace(path, @"\{[^}]+\}", "x");
            using var request = new HttpRequestMessage(new HttpMethod(method), concrete);
            request.Headers.Add("Origin", CsrfClient.Origin); // from our own page, but with no token
            var response = await client.SendAsync(request, Token);
            if (response.StatusCode != HttpStatusCode.Forbidden)
            {
                open.Add($"{method} {path} -> {(int)response.StatusCode}");
            }
        }

        Assert.Empty(open);
    }

    /// <summary>The same routes also refuse a request that does not say where it comes from, and one from another site.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("http://evil.example")]
    public async Task A_Request_That_Changes_State_Needs_An_Origin_Of_The_Application(string? origin)
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/messages/batch");
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        var response = await client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/health")]
    [InlineData("/login")]
    public async Task The_Server_Does_Not_Tell_Its_Name_And_Version(string path)
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path, Token);

        Assert.False(response.Headers.Contains("Server"));
        Assert.False(response.Headers.Contains("X-Powered-By"));
    }

    [Theory]
    [InlineData("/api/v1/health")]
    [InlineData("/api/v1/folders")] // refused, and the refusal is not kept either
    [InlineData("/api/v1/definitely-not-here")]
    public async Task What_The_Api_Answers_Is_Not_Kept_By_Any_Cache(string path)
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path, Token);

        Assert.Equal("no-store", Assert.Single(response.Headers.GetValues("Cache-Control")));
    }

    [Fact]
    public async Task The_Pages_Of_The_Application_Are_Not_Given_The_Cache_Rule_Of_The_Api()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/login", Token);

        Assert.DoesNotContain("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("mail.example.com", false)]
    [InlineData("10.0.0.5", false)]
    public void Only_The_Machine_Itself_Counts_As_Not_Needing_Encryption(string host, bool local) =>
        Assert.Equal(local, TransportSecurityWarnings.IsThisMachine(host));

    [Theory]
    [InlineData("mail.example.com", "None", true, "is None")]
    [InlineData("localhost", "None", false, "is None")]
    [InlineData("mail.example.com", "Tls", false, "is None")]
    [InlineData("mail.example.com", "StartTls", false, "is None")]
    public void An_Unencrypted_Connection_To_Another_Machine_Is_Warned_About_At_Start(string host, string security, bool warned, string phrase)
    {
        var logs = Start(("Imap:Host", host), ("Imap:Security", security), ("Smtp:Security", "StartTls"));

        Assert.Equal(warned, logs.Any(l => l.Contains(phrase, StringComparison.Ordinal) && l.Contains("Imap:Security", StringComparison.Ordinal)));
        Assert.DoesNotContain(logs, l => l.Contains("Smtp:Security is None", StringComparison.Ordinal));
    }

    [Fact]
    public void A_Certificate_Check_That_Is_Turned_Off_Is_Warned_About_And_Otherwise_Silent()
    {
        var off = Start(("Imap:AllowInvalidCertificate", "true"));
        var on = Start();

        Assert.Contains(off, l => l.Contains("Imap:AllowInvalidCertificate is on", StringComparison.Ordinal));
        Assert.DoesNotContain(on, l => l.Contains("AllowInvalidCertificate is on", StringComparison.Ordinal));
    }

    [Fact]
    public void The_Warnings_Never_Hold_A_Password()
    {
        var logs = Start(("Imap:Host", "mail.example.com"), ("Imap:Security", "None"), ("Smtp:Host", "mail.example.com"), ("Smtp:Security", "None"));

        Assert.Equal(2, logs.Count(l => l.Contains("travel unencrypted", StringComparison.Ordinal)));
        Assert.DoesNotContain(logs, l => l.Contains("password=", StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> Start(params (string Key, string Value)[] settings)
    {
        var logs = new List<string>();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            foreach (var (key, value) in settings)
            {
                b.UseSetting(key, value);
            }

            b.ConfigureServices(s => s.AddLogging(l => l.AddProvider(new ListLoggerProvider(logs))));
        });
        using var client = factory.CreateClient(); // starts the host: the hosted services say what they have to say
        return [.. logs];
    }

    private sealed class ListLoggerProvider(List<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ListLogger(lines);

        public void Dispose()
        {
        }

        private sealed class ListLogger(List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (lines)
                {
                    lines.Add(formatter(state, exception));
                }
            }
        }
    }
}
