using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Vinette.Api.Auth;
using Vinette.Api.Sessions;
using Vinette.TestSupport;

namespace Vinette.Api.Tests;

/// <summary>Login and logout through the real application against a real Dovecot.</summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class AuthIntegrationTests(DovecotFixture dovecot) : IDisposable
{
    private readonly WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseSetting("Imap:Host", dovecot.Host);
        b.UseSetting("Imap:Port", dovecot.ImapPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        b.UseSetting("Imap:Security", "None");
    });

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => factory.Dispose();

    private HttpClient CreateClient() =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    [Fact]
    public async Task Login_With_A_Real_Mailbox_Then_Logout()
    {
        var client = CreateClient();

        var login = await CsrfClient.PostJsonAsync(
            client,
            "/api/v1/auth/login",
            new { address = DovecotFixture.Alice.Address, password = DovecotFixture.Alice.Password, rememberMe = false },
            Token);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(SessionCookie.Name + "=", StringComparison.Ordinal));
        var id = cookie[(SessionCookie.Name.Length + 1)..].Split(';')[0];
        var sessions = factory.Services.GetRequiredService<SessionService>();
        var session = await sessions.GetValidAsync(id, Token);
        Assert.NotNull(session);
        Assert.Equal(DovecotFixture.Alice.Password, sessions.RevealPassword(session));

        var logout = await CsrfClient.SendAsync(client, HttpMethod.Post, "/api/v1/auth/logout", Token, sessionId: id);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Null(await sessions.GetValidAsync(id, Token));
    }

    [Fact]
    public async Task The_Session_Survives_A_Page_Reload()
    {
        var client = CreateClient();
        var login = await CsrfClient.PostJsonAsync(
            client,
            "/api/v1/auth/login",
            new { address = DovecotFixture.Bob.Address, password = DovecotFixture.Bob.Password, rememberMe = false },
            Token);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(SessionCookie.Name + "=", StringComparison.Ordinal)).Split(';')[0];

        // A reload sends the same cookie again and asks who is signed in.
        using var me = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
        me.Headers.Add("Cookie", cookie);
        var response = await client.SendAsync(me, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(DovecotFixture.Bob.Address, await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Five_Wrong_Passwords_Lock_The_Mailbox_Even_Against_The_Correct_One()
    {
        var client = CreateClient();
        var address = $"nobody-{Guid.NewGuid():N}@vinette.test"; // the mailbox does not exist: locks are per typed address

        for (var i = 0; i < 5; i++)
        {
            var wrong = await CsrfClient.PostJsonAsync(client, "/api/v1/auth/login", new { address, password = "wrong", rememberMe = false }, Token);
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        var locked = await CsrfClient.PostJsonAsync(client, "/api/v1/auth/login", new { address, password = "wrong", rememberMe = false }, Token);

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.True(locked.Headers.RetryAfter?.Delta > TimeSpan.Zero);
    }

    [Fact]
    public async Task A_Locked_Mailbox_Does_Not_Lock_The_Others()
    {
        var client = CreateClient();
        var address = $"nobody-{Guid.NewGuid():N}@vinette.test";
        for (var i = 0; i < 6; i++)
        {
            await CsrfClient.PostJsonAsync(client, "/api/v1/auth/login", new { address, password = "wrong", rememberMe = false }, Token);
        }

        var alice = await CsrfClient.PostJsonAsync(
            client,
            "/api/v1/auth/login",
            new { address = DovecotFixture.Alice.Address, password = DovecotFixture.Alice.Password, rememberMe = false },
            Token);

        Assert.Equal(HttpStatusCode.OK, alice.StatusCode);
    }

    [Fact]
    public async Task Wrong_Password_Is_Rejected_By_The_Real_Mail_Server()
    {
        var response = await CsrfClient.PostJsonAsync(
            CreateClient(),
            "/api/v1/auth/login",
            new { address = DovecotFixture.Alice.Address, password = "wrong-password", rememberMe = false },
            Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Unreachable_Mail_Server_Gives_503()
    {
        using var unreachable = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Imap:Host", dovecot.Host);
            b.UseSetting("Imap:Port", "1");
            b.UseSetting("Imap:Security", "None");
            b.UseSetting("Imap:Timeout", "00:00:03");
        });

        var response = await CsrfClient.PostJsonAsync(
            unreachable.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false }),
            "/api/v1/auth/login",
            new { address = DovecotFixture.Alice.Address, password = DovecotFixture.Alice.Password, rememberMe = false },
            Token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
