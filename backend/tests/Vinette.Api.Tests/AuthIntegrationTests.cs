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

        var login = await client.PostAsJsonAsync(
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

        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        logoutRequest.Headers.Add("Cookie", $"{SessionCookie.Name}={id}");
        var logout = await client.SendAsync(logoutRequest, Token);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Null(await sessions.GetValidAsync(id, Token));
    }

    [Fact]
    public async Task Wrong_Password_Is_Rejected_By_The_Real_Mail_Server()
    {
        var response = await CreateClient().PostAsJsonAsync(
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

        var response = await unreachable.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/login",
            new { address = DovecotFixture.Alice.Address, password = DovecotFixture.Alice.Password, rememberMe = false },
            Token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
