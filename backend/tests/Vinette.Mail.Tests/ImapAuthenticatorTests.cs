using System.Net;
using System.Net.Sockets;
using Vinette.Core;
using Vinette.Core.Authentication;

namespace Vinette.Mail.Tests;

/// <summary>Behavior that needs no mail server: input checks, unreachable servers, timeouts, client identity.</summary>
public class ImapAuthenticatorTests
{
    private static readonly ClientInfo NoClient = new(null, null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static int GetClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static ImapAuthenticator CreateAuthenticator(int port, TimeSpan? timeout = null) =>
        new(new ImapConnectionSettings("127.0.0.1", port, ConnectionSecurity.None, timeout ?? TimeSpan.FromSeconds(5)));

    [Theory]
    [InlineData("", "password")]
    [InlineData("   ", "password")]
    [InlineData("alice@vinette.test", "")]
    [InlineData("alice@vinette.test\r\nA2 LOGOUT", "password")]
    [InlineData("alice@vinette.test", "pass\0word")]
    public async Task Unusable_Input_Is_Rejected_Without_Contacting_The_Server(string address, string password)
    {
        // The port is closed: had the authenticator connected, the outcome would be ServerUnavailable.
        var authenticator = CreateAuthenticator(GetClosedPort());

        var result = await authenticator.AuthenticateAsync(new LoginRequest(address, password), NoClient, Token);

        Assert.Equal(AuthenticationOutcome.InvalidCredentials, result.Outcome);
    }

    [Fact]
    public async Task Unreachable_Server_Is_Reported_As_Unavailable_Without_Leaking_The_Password()
    {
        var authenticator = CreateAuthenticator(GetClosedPort());

        var result = await authenticator.AuthenticateAsync(new LoginRequest("alice@vinette.test", "s3cret-pass"), NoClient, Token);

        Assert.Equal(AuthenticationOutcome.ServerUnavailable, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Diagnostic));
        Assert.DoesNotContain("s3cret-pass", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Silent_Server_Times_Out_As_Unavailable()
    {
        // Accepts the connection but never sends the IMAP greeting.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var authenticator = CreateAuthenticator(port, TimeSpan.FromMilliseconds(800));

        var result = await authenticator.AuthenticateAsync(new LoginRequest("alice@vinette.test", "password"), NoClient, Token);

        Assert.Equal(AuthenticationOutcome.ServerUnavailable, result.Outcome);
    }

    [Fact]
    public async Task Cancellation_By_The_Caller_Is_Not_Reported_As_An_Outcome()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var authenticator = CreateAuthenticator(port, TimeSpan.FromSeconds(30));
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            authenticator.AuthenticateAsync(new LoginRequest("alice@vinette.test", "password"), NoClient, cancelled.Token));
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    public void Identity_Carries_A_Valid_Client_Ip(string ip, string expected)
    {
        var identity = ImapAuthenticator.BuildIdentity(new ClientInfo(ip, "Mozilla/5.0"));

        Assert.Equal("Vinette", identity.Name);
        Assert.Equal(expected, identity.Properties["x-originating-ip"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-ip")]
    [InlineData("203.0.113.7\r\nA2 LOGOUT")]
    public void Identity_Omits_A_Missing_Or_Malformed_Client_Ip(string? ip)
    {
        var identity = ImapAuthenticator.BuildIdentity(new ClientInfo(ip, null));

        Assert.False(identity.Properties.ContainsKey("x-originating-ip"));
    }
}
