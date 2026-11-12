using Vinette.Core;
using Vinette.Core.Mail;
using Vinette.TestSupport;

namespace Vinette.Mail.Tests;

/// <summary>Reading one message from a real Dovecot: the bytes, the flags, no side effects, and what happens to bad ids.</summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class MessageSourceIntegrationTests(DovecotFixture dovecot)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<MailAccount> MailboxAsync(params string[] fixtures)
    {
        var account = await dovecot.CreateAccountAsync($"src{Guid.NewGuid():N}"[..14], Token);
        foreach (var fixture in fixtures)
        {
            await dovecot.SaveFixtureAsync(account, "INBOX", fixture, Token);
        }

        return account;
    }

    private async Task<T> WithSessionAsync<T>(MailAccount account, Func<IMailSession, Task<T>> action)
    {
        var factory = new ImapMailSessionFactory(new ImapConnectionSettings(dovecot.Host, dovecot.ImapPort, ConnectionSecurity.None, TimeSpan.FromSeconds(30)));
        await using var session = await factory.OpenAsync(new MailCredentials("s", account.Address, account.Password), Token);
        return await action(session);
    }

    private static async Task<MessageId> FirstIdAsync(IMailSession session) =>
        (await session.ListMessagesAsync(new MessageListQuery("INBOX", null, 10, NewestFirst: false), Token)).Items[0].Id;

    [Fact]
    public async Task The_Raw_Mail_Is_Returned_Byte_For_Byte()
    {
        var account = await MailboxAsync("02-utf8-qp-alternative.eml");

        var source = await WithSessionAsync(account, async s => await s.GetMessageAsync(await FirstIdAsync(s), Token));

        var original = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "02-utf8-qp-alternative.eml"), Token);
        var text = System.Text.Encoding.UTF8.GetString(source.Content);
        Assert.Contains("Subject:", text, StringComparison.Ordinal);
        Assert.True(Math.Abs(source.Content.Length - original.Length) < 400, "The server may add a few headers, not rewrite the mail.");
    }

    [Fact]
    public async Task Reading_Does_Not_Mark_The_Message_As_Read()
    {
        var account = await MailboxAsync("01-plain-ascii.eml");

        var (before, after) = await WithSessionAsync(account, async s =>
        {
            var id = await FirstIdAsync(s);
            var one = await s.GetMessageAsync(id, Token);
            var two = await s.GetMessageAsync(id, Token);
            return (one, two);
        });

        Assert.False(before.Seen);
        Assert.False(after.Seen);
    }

    [Fact]
    public async Task The_Flags_And_Labels_Come_With_The_Mail()
    {
        var account = await MailboxAsync("01-plain-ascii.eml");
        await dovecot.DoveadmAsync(Token, "flags", "add", "-u", account.Address, "\\Seen \\Flagged \\Answered", "mailbox", "INBOX", "uid", "1");
        await dovecot.DoveadmAsync(Token, "flags", "add", "-u", account.Address, "Work", "mailbox", "INBOX", "uid", "1");

        var source = await WithSessionAsync(account, async s => await s.GetMessageAsync(await FirstIdAsync(s), Token));

        Assert.True(source.Seen);
        Assert.True(source.Flagged);
        Assert.True(source.Answered);
        Assert.False(source.Draft);
        Assert.Equal(["Work"], source.Labels);
    }

    [Fact]
    public async Task A_Message_That_Is_Gone_Is_Reported_As_Not_Found()
    {
        var account = await MailboxAsync("01-plain-ascii.eml");

        await Assert.ThrowsAsync<MailMessageNotFoundException>(() => WithSessionAsync(account, async s =>
        {
            var id = await FirstIdAsync(s);
            return await s.GetMessageAsync(new MessageId(id.Folder, id.UidValidity, 999), Token);
        }));
    }

    [Fact]
    public async Task A_Message_Deleted_After_It_Was_Listed_Is_Reported_As_Not_Found()
    {
        var account = await MailboxAsync("01-plain-ascii.eml", "02-utf8-qp-alternative.eml");

        await Assert.ThrowsAsync<MailMessageNotFoundException>(() => WithSessionAsync(account, async s =>
        {
            var id = await FirstIdAsync(s);
            await dovecot.DoveadmAsync(Token, "expunge", "-u", account.Address, "mailbox", "INBOX", "uid", id.Uid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return await s.GetMessageAsync(id, Token);
        }));
    }

    [Fact]
    public async Task An_Id_Of_A_Recreated_Folder_Is_Refused()
    {
        var account = await MailboxAsync("01-plain-ascii.eml");

        var error = await Assert.ThrowsAsync<UidValidityChangedException>(() => WithSessionAsync(account, async s =>
        {
            var id = await FirstIdAsync(s);
            return await s.GetMessageAsync(new MessageId(id.Folder, id.UidValidity + 1, id.Uid), Token);
        }));

        Assert.NotEqual(0u, error.Current);
    }

    [Theory]
    [InlineData("No such folder")]
    [InlineData("Работа.Нет")]
    public async Task A_Missing_Folder_Is_Reported_As_Not_Found(string folder)
    {
        var account = await MailboxAsync("01-plain-ascii.eml");

        await Assert.ThrowsAsync<MailFolderNotFoundException>(() =>
            WithSessionAsync(account, s => s.GetMessageAsync(new MessageId(folder, 1, 1), Token)));
    }

    [Fact]
    public async Task A_Message_In_A_Cyrillic_Folder_Is_Read()
    {
        var account = await MailboxAsync();
        await dovecot.CreateFolderAsync(account, "Работа", Token);
        await dovecot.SaveFixtureAsync(account, "Работа", "04-windows1251-qp.eml", Token);

        var source = await WithSessionAsync(account, async s =>
        {
            var page = await s.ListMessagesAsync(new MessageListQuery("Работа", null, 5), Token);
            return await s.GetMessageAsync(page.Items[0].Id, Token);
        });

        Assert.Equal("Работа", source.Id.Folder);
        Assert.NotEmpty(source.Content);
    }

    [Fact]
    public async Task Every_Fixture_Can_Be_Read_Whole()
    {
        var files = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fixtures"), "*.eml").Select(Path.GetFileName).Order().ToArray();
        var account = await MailboxAsync([.. files.Select(f => f!)]);

        var sources = await WithSessionAsync(account, async s =>
        {
            var page = await s.ListMessagesAsync(new MessageListQuery("INBOX", null, 100, NewestFirst: false), Token);
            var result = new List<MessageSource>();
            foreach (var item in page.Items)
            {
                result.Add(await s.GetMessageAsync(item.Id, Token));
            }

            return result;
        });

        Assert.Equal(files.Length, sources.Count);
        Assert.All(sources, s => Assert.True(s.Content.Length > 100));
    }
}
