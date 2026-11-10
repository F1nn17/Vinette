using Vinette.Core;
using Vinette.Core.Mail;
using Vinette.TestSupport;

namespace Vinette.Mail.Tests;

/// <summary>Mailbox quota with servers that do not report one or answer oddly, against a scripted server.</summary>
public sealed class QuotaTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<IMailSession> OpenAsync(int port) =>
        await new ImapMailSessionFactory(new ImapConnectionSettings("127.0.0.1", port, ConnectionSecurity.None, TimeSpan.FromSeconds(10)))
            .OpenAsync(new MailCredentials("s", "alice@vinette.test", "pw"), Token);

    [Fact]
    public async Task A_Server_Without_The_Quota_Extension_Gives_Nothing_To_Show_And_Is_Not_Asked()
    {
        await using var server = new ScriptedImapServer("IDLE");
        await using var session = await OpenAsync(server.Port);

        var quota = await session.GetQuotaAsync(Token);

        Assert.Null(quota);
        Assert.DoesNotContain("GETQUOTAROOT", server.Commands);
    }

    [Fact]
    public async Task A_Mailbox_Without_A_Storage_Limit_Gives_Nothing_To_Show()
    {
        await using var server = new ScriptedImapServer("QUOTA", new()
        {
            ["GETQUOTAROOT"] = ["* QUOTAROOT \"INBOX\" \"\"", "* QUOTA \"\" (MESSAGE 5 1000)", "{tag} OK done"],
        });
        await using var session = await OpenAsync(server.Port);

        Assert.Null(await session.GetQuotaAsync(Token)); // only a message-count limit: no storage figure
    }

    [Fact]
    public async Task A_Refused_Quota_Request_Gives_Nothing_To_Show()
    {
        await using var server = new ScriptedImapServer("QUOTA", new()
        {
            ["GETQUOTAROOT"] = ["{tag} NO [NOPERM] not allowed"],
        });
        await using var session = await OpenAsync(server.Port);

        Assert.Null(await session.GetQuotaAsync(Token));
    }

    [Fact]
    public async Task The_Storage_Figures_Of_The_Server_Are_Converted_From_Kilobytes_To_Bytes()
    {
        await using var server = new ScriptedImapServer("QUOTA", new()
        {
            ["GETQUOTAROOT"] = ["* QUOTAROOT \"INBOX\" \"\"", "* QUOTA \"\" (STORAGE 2048 10240)", "{tag} OK done"],
        });
        await using var session = await OpenAsync(server.Port);

        var quota = await session.GetQuotaAsync(Token);

        Assert.Equal(new MailQuota(2048L * 1024, 10240L * 1024), quota);
        Assert.Equal(0.2, quota!.UsedFraction, 3);
    }

    [Fact]
    public void An_Empty_Limit_Does_Not_Divide_By_Zero()
    {
        Assert.Equal(0, new MailQuota(100, 0).UsedFraction);
    }
}

/// <summary>Mailbox quota against a real Dovecot.</summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class QuotaIntegrationTests(DovecotFixture dovecot)
{
    [Fact]
    public async Task Dovecot_Reports_The_Storage_Use_Of_A_Mailbox_With_A_Limit()
    {
        var token = TestContext.Current.CancellationToken;
        var account = await dovecot.CreateAccountAsync($"quota{Guid.NewGuid():N}"[..16], token);
        await dovecot.SetQuotaAsync(account, "10M", token);
        await dovecot.SaveFixtureAsync(account, "INBOX", "01-plain-ascii.eml", token);
        var factory = new ImapMailSessionFactory(new ImapConnectionSettings(dovecot.Host, dovecot.ImapPort, ConnectionSecurity.None, TimeSpan.FromSeconds(30)));
        await using var session = await factory.OpenAsync(new MailCredentials("s", account.Address, account.Password), token);

        var quota = await session.GetQuotaAsync(token);

        Assert.NotNull(quota);
        Assert.Equal(10L * 1024 * 1024, quota.LimitBytes);
        Assert.InRange(quota.UsedBytes, 1, 1024 * 1024); // one small message
    }
}
