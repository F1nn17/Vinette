using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Vinette.Core.Authentication;

namespace Vinette.Api.Tests;

public sealed class ForwardedHeadersTests : IDisposable
{
    private sealed class RecordingAuthenticator : IAuthenticator
    {
        public List<ClientInfo> Clients { get; } = [];

        public Task<AuthenticationResult> AuthenticateAsync(LoginRequest request, ClientInfo client, CancellationToken cancellationToken)
        {
            Clients.Add(client);
            return Task.FromResult(request.Password == "right" ? AuthenticationResult.Success : AuthenticationResult.InvalidCredentials);
        }
    }

    /// <summary>Makes the test server look like a connection from <c>peer</c> (it has no real peer address).</summary>
    private sealed class PeerAddressFilter(Func<string> peer) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(peer());
                return nextMiddleware(context);
            });
            next(app);
        };
    }

    private readonly RecordingAuthenticator authenticator = new();
    private readonly List<WebApplicationFactory<Program>> factories = [];
    private string peer = "10.0.0.5";

    public void Dispose()
    {
        foreach (var factory in factories)
        {
            factory.Dispose();
        }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private HttpClient CreateClient(params string[] trustedProxies)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            for (var i = 0; i < trustedProxies.Length; i++)
            {
                b.UseSetting($"Security:TrustedProxies:{i}", trustedProxies[i]);
            }

            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuthenticator>();
                services.AddSingleton<IAuthenticator>(authenticator);
                services.AddSingleton<IStartupFilter>(new PeerAddressFilter(() => peer));
            });
        });
        factories.Add(factory);
        return factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string password, IReadOnlyDictionary<string, string>? headers = null) =>
        CsrfClient.PostJsonAsync(client, "/api/v1/auth/login", new { address = "alice@vinette.test", password, rememberMe = false }, Token, extraHeaders: headers);

    private static Dictionary<string, string> Forwarded(string forwardedFor, string? proto = null, string? host = null)
    {
        var headers = new Dictionary<string, string> { ["X-Forwarded-For"] = forwardedFor };
        if (proto is not null)
        {
            headers["X-Forwarded-Proto"] = proto;
        }

        if (host is not null)
        {
            headers["X-Forwarded-Host"] = host;
        }

        return headers;
    }

    // ---- Who is the client ----

    [Fact]
    public async Task Without_Trusted_Proxies_Forwarded_Headers_Are_Ignored()
    {
        var client = CreateClient();

        await LoginAsync(client, "right", Forwarded("203.0.113.5"));

        Assert.Equal("10.0.0.5", Assert.Single(authenticator.Clients).IpAddress);
    }

    [Fact]
    public async Task A_Trusted_Proxy_Passes_The_Real_Client_Address_On()
    {
        var client = CreateClient("10.0.0.0/8");

        await LoginAsync(client, "right", Forwarded("203.0.113.5"));

        Assert.Equal("203.0.113.5", Assert.Single(authenticator.Clients).IpAddress);
    }

    [Fact]
    public async Task A_Single_Address_Can_Be_Trusted()
    {
        var client = CreateClient("10.0.0.5");

        await LoginAsync(client, "right", Forwarded("203.0.113.5"));

        Assert.Equal("203.0.113.5", Assert.Single(authenticator.Clients).IpAddress);
    }

    [Fact]
    public async Task A_Forged_Address_At_The_Start_Of_The_Chain_Is_Not_Believed()
    {
        var client = CreateClient("10.0.0.0/8");

        // The client sent "6.6.6.6"; the proxy appended the address it really saw.
        await LoginAsync(client, "right", Forwarded("6.6.6.6, 203.0.113.5"));

        Assert.Equal("203.0.113.5", Assert.Single(authenticator.Clients).IpAddress);
    }

    [Fact]
    public async Task Several_Trusted_Hops_Are_Skipped_To_Reach_The_Client()
    {
        var client = CreateClient("10.0.0.0/8");

        await LoginAsync(client, "right", Forwarded("203.0.113.5, 10.1.2.3"));

        Assert.Equal("203.0.113.5", Assert.Single(authenticator.Clients).IpAddress);
    }

    [Fact]
    public async Task A_Peer_Outside_The_Trusted_List_Cannot_Choose_Its_Address()
    {
        peer = "192.0.2.77";
        var client = CreateClient("10.0.0.0/8");

        await LoginAsync(client, "right", Forwarded("203.0.113.5"));

        Assert.Equal("192.0.2.77", Assert.Single(authenticator.Clients).IpAddress);
    }

    [Fact]
    public async Task Loopback_Is_Not_Trusted_Unless_Listed()
    {
        peer = "127.0.0.1";
        var client = CreateClient("10.0.0.0/8");

        await LoginAsync(client, "right", Forwarded("203.0.113.5"));

        Assert.Equal("127.0.0.1", Assert.Single(authenticator.Clients).IpAddress);
    }

    [Fact]
    public async Task Ipv6_Clients_And_Networks_Work()
    {
        peer = "fd00::5";
        var client = CreateClient("fd00::/8");

        await LoginAsync(client, "right", Forwarded("2001:db8::7"));

        Assert.Equal("2001:db8::7", Assert.Single(authenticator.Clients).IpAddress);
    }

    // ---- What depends on it ----

    [Fact]
    public async Task Clients_Behind_One_Proxy_Do_Not_Share_The_Failure_Limit()
    {
        var client = CreateClient("10.0.0.0/8");

        for (var i = 0; i < 20; i++)
        {
            await CsrfClient.PostJsonAsync(
                client,
                "/api/v1/auth/login",
                new { address = $"user{i}@vinette.test", password = "wrong", rememberMe = false },
                Token,
                extraHeaders: Forwarded("203.0.113.5"));
        }

        var blocked = await LoginAsync(client, "right", Forwarded("203.0.113.5"));
        var other = await LoginAsync(client, "right", Forwarded("203.0.113.6"));

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task The_Public_Host_From_A_Trusted_Proxy_Satisfies_The_Origin_Check()
    {
        var client = CreateClient("10.0.0.0/8");
        var headers = Forwarded("203.0.113.5", "https", "mail.example.org");
        headers["Origin"] = "https://mail.example.org";

        var accepted = await CsrfClient.SendAsync(client, HttpMethod.Post, "/api/v1/auth/logout", Token, extraHeaders: headers);

        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
    }

    [Fact]
    public async Task A_Forwarded_Host_From_An_Untrusted_Source_Does_Not_Open_The_Origin_Check()
    {
        var client = CreateClient(); // nobody is trusted
        var headers = Forwarded("203.0.113.5", "https", "mail.example.org");
        headers["Origin"] = "https://mail.example.org";

        var refused = await CsrfClient.SendAsync(client, HttpMethod.Post, "/api/v1/auth/logout", Token, extraHeaders: headers);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    // ---- Configuration ----

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("not-an-address")]
    [InlineData("10.0.0.0/33")]
    [InlineData("proxy.example.org")]
    public void Unusable_Trusted_Proxies_Fail_Startup(string entry)
    {
        StartupFailure.AssertOptionsInvalid(b => b.UseSetting("Security:TrustedProxies:0", entry));
    }
}
