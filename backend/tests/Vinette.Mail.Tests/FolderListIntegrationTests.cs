using Vinette.Core;
using Vinette.Core.Mail;
using Vinette.TestSupport;

namespace Vinette.Mail.Tests;

/// <summary>The folder list against a real Dovecot: special-use roles, hierarchy and counters.</summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class FolderListIntegrationTests(DovecotFixture dovecot)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<IReadOnlyList<MailFolderInfo>> ListAsync(MailAccount account)
    {
        var factory = new ImapMailSessionFactory(new ImapConnectionSettings(dovecot.Host, dovecot.ImapPort, ConnectionSecurity.None, TimeSpan.FromSeconds(30)));
        await using var session = await factory.OpenAsync(new MailCredentials("s", account.Address, account.Password), Token);
        return await session.GetFoldersAsync(Token);
    }

    [Fact]
    public async Task The_Special_Folders_Of_Dovecot_Are_Recognized_By_Their_Attributes()
    {
        var folders = await ListAsync(DovecotFixture.Alice);

        var roles = folders.Where(f => f.Role != FolderRole.Other).ToDictionary(f => f.Role, f => f.Path);
        Assert.Equal("INBOX", roles[FolderRole.Inbox]);
        Assert.Equal("Drafts", roles[FolderRole.Drafts]);
        Assert.Equal("Sent", roles[FolderRole.Sent]);
        Assert.Equal("Junk", roles[FolderRole.Junk]);
        Assert.Equal("Trash", roles[FolderRole.Trash]);
        Assert.Single(folders, f => f.Path == "INBOX"); // listed once, even though some servers list it twice
    }

    [Fact]
    public async Task The_Inbox_Counts_Every_Message_And_Unread_Ones()
    {
        var folders = await ListAsync(DovecotFixture.Bob);

        var inbox = Assert.Single(folders, f => f.Role == FolderRole.Inbox);
        Assert.Equal(dovecot.SeededMessageCount, inbox.Total);
        Assert.Equal(dovecot.SeededMessageCount, inbox.Unread);
        Assert.True(inbox.CanContainMessages);
        Assert.True(inbox.Subscribed);
    }

    [Fact]
    public async Task Nested_And_Cyrillic_Folders_Come_With_Their_Parent_And_Counters()
    {
        var account = await dovecot.CreateAccountAsync($"folders{Guid.NewGuid():N}"[..18], Token);
        await dovecot.CreateFolderAsync(account, "Работа", Token);
        await dovecot.CreateFolderAsync(account, "Работа.Отчёты", Token);
        await dovecot.SaveFixtureAsync(account, "Работа.Отчёты", "01-plain-ascii.eml", Token);
        await dovecot.SaveFixtureAsync(account, "Работа.Отчёты", "02-utf8-qp-alternative.eml", Token);
        await dovecot.SaveFixtureAsync(account, "Работа.Отчёты", "05-utf8-base64.eml", Token);
        await dovecot.MarkFolderReadAsync(account, "Работа.Отчёты", Token);
        await dovecot.SaveFixtureAsync(account, "Работа.Отчёты", "13-html-only.eml", Token);

        var folders = await ListAsync(account);

        var parent = Assert.Single(folders, f => f.Name == "Работа");
        Assert.Null(parent.ParentPath);
        var child = Assert.Single(folders, f => f.Name == "Отчёты");
        Assert.Equal("Работа", child.ParentPath);
        Assert.Equal(4, child.Total);
        Assert.Equal(1, child.Unread);
        Assert.Equal(FolderRole.Other, child.Role);
        Assert.Equal(["Работа", "Работа.Отчёты"], FolderOrder.Sort(folders).Select(f => f.Path).Where(p => p.StartsWith("Работа", StringComparison.Ordinal)).ToArray());
    }
}
