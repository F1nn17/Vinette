using System.Net;
using System.Net.Sockets;
using Vinette.Core;
using Vinette.Core.Mail;
using Vinette.TestSupport;

namespace Vinette.Mail.Tests;

/// <summary>Long-lived IMAP connections against a real Dovecot, alone and through the connection manager.</summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class ImapMailSessionFactoryIntegrationTests(DovecotFixture dovecot)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private ImapMailSessionFactory Create(int? port = null) =>
        new(new ImapConnectionSettings(dovecot.Host, port ?? dovecot.ImapPort, ConnectionSecurity.None, TimeSpan.FromSeconds(30)));

    private static MailCredentials Alice(string session = "s1", string? password = null) =>
        new(session, DovecotFixture.Alice.Address, password ?? DovecotFixture.Alice.Password, "203.0.113.7");

    [Fact]
    public async Task A_Session_Stays_Open_And_Answers_Commands()
    {
        await using var session = await Create().OpenAsync(Alice(), Token);

        await session.PingAsync(Token);
        await session.PingAsync(Token);

        Assert.True(session.IsConnected);
    }

    [Fact]
    public async Task A_Disposed_Session_Is_Not_Connected()
    {
        var session = await Create().OpenAsync(Alice(), Token);

        await session.DisposeAsync();

        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task A_Rejected_Password_Is_Reported_As_Revoked_Access()
    {
        var exception = await Assert.ThrowsAsync<MailAccessRevokedException>(() =>
            Create().OpenAsync(Alice(password: "wrong-password"), Token));

        Assert.DoesNotContain("wrong-password", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_Unreachable_Server_Is_Reported_As_Unavailable()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        await Assert.ThrowsAsync<MailUnavailableException>(() =>
            new ImapMailSessionFactory(new ImapConnectionSettings("127.0.0.1", port, ConnectionSecurity.None, TimeSpan.FromSeconds(5)))
                .OpenAsync(Alice(), Token));
    }

    [Fact]
    public async Task The_Manager_Serves_Parallel_Requests_Of_One_User_With_One_Connection()
    {
        await using var manager = new MailConnectionManager(Create());

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            manager.ExecuteAsync(Alice(), async (session, token) =>
            {
                await session.PingAsync(token);
                return session;
            }, Token)));

        Assert.Single(results.Distinct());
        Assert.Equal(1, manager.ConnectionCount);
    }

    [Fact]
    public async Task A_Command_Cancelled_Mid_Flight_Does_Not_Break_The_Next_One()
    {
        await using var manager = new MailConnectionManager(Create());
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.ExecuteAsync<int>(Alice(), async (session, token) =>
            {
                while (true)
                {
                    await session.PingAsync(token);
                }
            }, cancel.Token));
        await manager.ExecuteAsync(Alice(), (session, token) => session.PingAsync(token).ContinueWith(_ => 0, token), Token);

        Assert.Equal(1, manager.ConnectionCount);
    }
}
