using System.Diagnostics;
using Vinette.Core;
using Vinette.Core.Mail;
using Vinette.TestSupport;

namespace Vinette.Mail.Tests;

/// <summary>
/// What the connection manager does when the network or the mail server misbehaves, against a real Dovecot. A
/// <see cref="FlakyProxy"/> stands between them so a test can cut, slow down or hold the traffic; Dovecot itself is
/// restarted for the restart scenario.
/// </summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class ResilienceIntegrationTests(DovecotFixture dovecot)
{
    // Patient on purpose: Dovecot needs a few seconds to come back after a restart.
    private static readonly MailReconnectPolicy Patient = new(8, TimeSpan.FromMilliseconds(250), 1.6, 0.1);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private MailConnectionManager Manager(int port, TimeSpan? timeout = null, MailReconnectPolicy? policy = null, MailConnectionLimits? limits = null) =>
        new(
            new ImapMailSessionFactory(new ImapConnectionSettings(dovecot.Host, port, ConnectionSecurity.None, timeout ?? TimeSpan.FromSeconds(30))),
            policy ?? Patient,
            null,
            limits);

    private static MailCredentials Credentials(MailAccount account, string session = "s1") =>
        new(session, account.Address, account.Password, "203.0.113.7");

    private static Task<IMailSession> PingAsync(MailConnectionManager manager, MailCredentials credentials) =>
        manager.ExecuteAsync(
            credentials,
            async (session, token) =>
            {
                await session.PingAsync(token);
                return session;
            },
            Token);

    [Fact]
    public async Task A_Dropped_Network_Between_Two_Requests_Is_Invisible_To_The_User()
    {
        await using var proxy = new FlakyProxy(dovecot.Host, dovecot.ImapPort);
        await using var manager = Manager(proxy.Port);
        var first = await PingAsync(manager, Credentials(DovecotFixture.Alice));

        proxy.CutAll();
        var second = await PingAsync(manager, Credentials(DovecotFixture.Alice));

        Assert.NotSame(first, second); // a new connection was opened behind the scenes
        Assert.Equal(2, proxy.Accepted);
        Assert.True(second.IsConnected);
    }

    [Fact]
    public async Task A_Server_That_Is_Down_For_A_Few_Seconds_Is_Waited_For()
    {
        await using var proxy = new FlakyProxy(dovecot.Host, dovecot.ImapPort);
        await using var manager = Manager(proxy.Port);
        await PingAsync(manager, Credentials(DovecotFixture.Alice));
        proxy.CutAll();
        proxy.Refuse(true);
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2), Token);
            proxy.Refuse(false);
        }, Token);

        var session = await PingAsync(manager, Credentials(DovecotFixture.Alice));

        Assert.True(session.IsConnected);
    }

    [Fact]
    public async Task A_Server_That_Stays_Down_Gives_A_Clear_Error_After_The_Retries()
    {
        await using var proxy = new FlakyProxy(dovecot.Host, dovecot.ImapPort);
        proxy.Refuse(true);
        await using var manager = Manager(proxy.Port, policy: new MailReconnectPolicy(2, TimeSpan.FromMilliseconds(50), 2, 0));

        await Assert.ThrowsAsync<MailUnavailableException>(() => PingAsync(manager, Credentials(DovecotFixture.Alice)));
    }

    [Fact]
    public async Task A_Real_Dovecot_Restart_Is_Survived_By_An_Open_Connection()
    {
        await using var manager = Manager(dovecot.ImapPort);
        var before = await PingAsync(manager, Credentials(DovecotFixture.Alice));

        await dovecot.RestartDovecotAsync(Token);
        var after = await PingAsync(manager, Credentials(DovecotFixture.Alice));

        Assert.NotSame(before, after);
        Assert.True(after.IsConnected);
    }

    [Fact]
    public async Task A_Slow_Network_Is_Slow_But_Works()
    {
        await using var proxy = new FlakyProxy(dovecot.Host, dovecot.ImapPort);
        proxy.Delay(TimeSpan.FromMilliseconds(150));
        await using var manager = Manager(proxy.Port, TimeSpan.FromSeconds(20));
        var watch = Stopwatch.StartNew();

        var session = await PingAsync(manager, Credentials(DovecotFixture.Alice));
        await session.PingAsync(Token);

        Assert.True(watch.Elapsed > TimeSpan.FromMilliseconds(300), "the delay was not applied");
        Assert.Equal(1, proxy.Accepted);
    }

    [Fact]
    public async Task A_Server_That_Stops_Answering_Is_Given_Up_On_After_The_Timeout_And_The_Next_Attempt_Works()
    {
        await using var proxy = new FlakyProxy(dovecot.Host, dovecot.ImapPort);
        await using var manager = Manager(proxy.Port, TimeSpan.FromSeconds(2));
        await PingAsync(manager, Credentials(DovecotFixture.Alice));
        proxy.Stall();
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3), Token);
            proxy.CutAll();
            proxy.Resume();
        }, Token);
        var watch = Stopwatch.StartNew();

        var session = await PingAsync(manager, Credentials(DovecotFixture.Alice));

        Assert.True(session.IsConnected);
        Assert.True(watch.Elapsed > TimeSpan.FromSeconds(1.5), "the stalled command was not noticed");
    }

    [Fact]
    public async Task Many_Sessions_Of_Two_Mailboxes_Work_At_The_Same_Time_Without_Mixing_Up()
    {
        await using var manager = Manager(dovecot.ImapPort, limits: new MailConnectionLimits(5, 20, TimeSpan.FromSeconds(30)));
        var accounts = new[] { DovecotFixture.Alice, DovecotFixture.Bob };

        var work = Enumerable.Range(0, 10).SelectMany(i =>
        {
            var account = accounts[i % 2];
            var credentials = Credentials(account, $"session-{i}");
            return Enumerable.Range(0, 10).Select(_ => Task.Run(() => PingAsync(manager, credentials), Token));
        }).ToArray();
        await Task.WhenAll(work);

        Assert.Equal(10, manager.ConnectionCount); // one connection per session, not one per command
        Assert.All(work, t => Assert.True(t.Result.IsConnected));
    }

    [Fact]
    public async Task A_Changed_Password_Ends_The_Session_At_The_Next_Reconnect_Without_Locking_Retries()
    {
        var account = await dovecot.CreateAccountAsync($"resilience{Guid.NewGuid():N}"[..20], Token);
        await using var proxy = new FlakyProxy(dovecot.Host, dovecot.ImapPort);
        await using var manager = Manager(proxy.Port);
        var credentials = Credentials(account);
        await PingAsync(manager, credentials);

        _ = await dovecot.ChangePasswordAsync(account, "changed-by-admin", Token);
        proxy.CutAll(); // the open connection goes away, the next command must sign in again with the old password

        await Assert.ThrowsAsync<MailAccessRevokedException>(() => PingAsync(manager, credentials));
        Assert.Equal(2, proxy.Accepted); // the first connection and exactly one more: a rejected password is not retried
    }
}
