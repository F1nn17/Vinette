using Vinette.Core;
using Vinette.Core.Mail;
using Vinette.TestSupport;

namespace Vinette.Mail.Tests;

/// <summary>Changing the read and flag marks of a message on a real Dovecot.</summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class MessageFlagsIntegrationTests(DovecotFixture dovecot)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<MailAccount> MailboxAsync(int count)
    {
        var account = await dovecot.CreateAccountAsync($"flg{Guid.NewGuid():N}"[..14], Token);
        for (var i = 0; i < count; i++)
        {
            await dovecot.SaveFixtureAsync(account, "INBOX", i % 2 == 0 ? "01-plain-ascii.eml" : "02-utf8-qp-alternative.eml", Token);
        }

        return account;
    }

    private async Task<T> WithSessionAsync<T>(MailAccount account, Func<IMailSession, Task<T>> action)
    {
        var factory = new ImapMailSessionFactory(new ImapConnectionSettings(dovecot.Host, dovecot.ImapPort, ConnectionSecurity.None, TimeSpan.FromSeconds(30)));
        await using var session = await factory.OpenAsync(new MailCredentials("s", account.Address, account.Password), Token);
        return await action(session);
    }

    private static async Task<IReadOnlyList<MessageListItem>> ListAsync(IMailSession session) =>
        (await session.ListMessagesAsync(new MessageListQuery("INBOX", null, 50, NewestFirst: false), Token)).Items;

    [Fact]
    public async Task A_Message_Is_Marked_As_Read_And_Unread_Again()
    {
        var account = await MailboxAsync(1);

        var (read, unread, listed) = await WithSessionAsync(account, async s =>
        {
            var id = (await ListAsync(s))[0].Id;
            var one = await s.UpdateFlagsAsync(id, new MessageFlagChange(true, null), Token);
            var afterRead = (await ListAsync(s))[0].Seen;
            var two = await s.UpdateFlagsAsync(id, new MessageFlagChange(false, null), Token);
            return (one, two, afterRead);
        });

        Assert.True(read.Seen);
        Assert.True(listed);
        Assert.False(unread.Seen);
    }

    [Fact]
    public async Task The_Flag_Is_Set_And_Removed_Without_Touching_The_Read_Mark()
    {
        var account = await MailboxAsync(1);

        var (flagged, plain) = await WithSessionAsync(account, async s =>
        {
            var id = (await ListAsync(s))[0].Id;
            await s.UpdateFlagsAsync(id, new MessageFlagChange(true, null), Token);
            var a = await s.UpdateFlagsAsync(id, new MessageFlagChange(null, true), Token);
            var b = await s.UpdateFlagsAsync(id, new MessageFlagChange(null, false), Token);
            return (a, b);
        });

        Assert.True(flagged.Flagged);
        Assert.True(flagged.Seen);
        Assert.False(plain.Flagged);
        Assert.True(plain.Seen);
    }

    [Fact]
    public async Task Both_Marks_Can_Change_In_One_Call()
    {
        var account = await MailboxAsync(1);

        var state = await WithSessionAsync(account, async s => await s.UpdateFlagsAsync((await ListAsync(s))[0].Id, new MessageFlagChange(true, true), Token));

        Assert.True(state.Seen);
        Assert.True(state.Flagged);
    }

    [Fact]
    public async Task Setting_A_Mark_That_Is_Already_Set_Is_Not_An_Error()
    {
        var account = await MailboxAsync(1);

        var state = await WithSessionAsync(account, async s =>
        {
            var id = (await ListAsync(s))[0].Id;
            await s.UpdateFlagsAsync(id, new MessageFlagChange(true, true), Token);
            return await s.UpdateFlagsAsync(id, new MessageFlagChange(true, true), Token);
        });

        Assert.True(state.Seen);
        Assert.True(state.Flagged);
    }

    [Fact]
    public async Task A_Change_Of_Nothing_Reports_The_Marks_As_They_Are()
    {
        var account = await MailboxAsync(1);
        await dovecot.DoveadmAsync(Token, "flags", "add", "-u", account.Address, "\\Answered \\Seen", "mailbox", "INBOX", "uid", "1");

        var state = await WithSessionAsync(account, async s => await s.UpdateFlagsAsync((await ListAsync(s))[0].Id, new MessageFlagChange(null, null), Token));

        Assert.Equal(new MessageFlagState(true, false, true, false), state);
    }

    [Fact]
    public async Task Other_Marks_And_Other_Messages_Are_Left_Alone()
    {
        var account = await MailboxAsync(3);
        await dovecot.DoveadmAsync(Token, "flags", "add", "-u", account.Address, "\\Answered", "mailbox", "INBOX", "uid", "2");
        await dovecot.DoveadmAsync(Token, "flags", "add", "-u", account.Address, "Work", "mailbox", "INBOX", "uid", "2");

        var items = await WithSessionAsync(account, async s =>
        {
            var middle = (await ListAsync(s))[1].Id;
            await s.UpdateFlagsAsync(middle, new MessageFlagChange(true, true), Token);
            return await ListAsync(s);
        });

        Assert.False(items[0].Seen);
        Assert.False(items[0].Flagged);
        Assert.True(items[1].Seen);
        Assert.True(items[1].Flagged);
        Assert.True(items[1].Answered); // the reply mark stays
        Assert.Equal(["Work"], items[1].Labels); // so does the label
        Assert.False(items[2].Seen);
    }

    [Fact]
    public async Task What_Is_Changed_Is_There_For_The_Next_Connection()
    {
        var account = await MailboxAsync(1);
        await WithSessionAsync(account, async s => await s.UpdateFlagsAsync((await ListAsync(s))[0].Id, new MessageFlagChange(true, true), Token));

        var item = await WithSessionAsync(account, async s => (await ListAsync(s))[0]);

        Assert.True(item.Seen);
        Assert.True(item.Flagged);
    }

    [Fact]
    public async Task Reading_Still_Works_After_The_Folder_Was_Opened_For_Writing()
    {
        var account = await MailboxAsync(1);

        var source = await WithSessionAsync(account, async s =>
        {
            var id = (await ListAsync(s))[0].Id;
            await s.UpdateFlagsAsync(id, new MessageFlagChange(true, null), Token);
            return await s.GetMessageAsync(id, Token);
        });

        Assert.True(source.Seen);
        Assert.NotEmpty(source.Content);
    }

    [Fact]
    public async Task A_Message_That_Is_Gone_Is_Reported_As_Not_Found()
    {
        var account = await MailboxAsync(1);

        await Assert.ThrowsAsync<MailMessageNotFoundException>(() => WithSessionAsync(account, async s =>
        {
            var id = (await ListAsync(s))[0].Id;
            return await s.UpdateFlagsAsync(new MessageId(id.Folder, id.UidValidity, 999), new MessageFlagChange(true, null), Token);
        }));
    }

    [Fact]
    public async Task An_Id_Of_A_Recreated_Folder_Is_Refused_And_Nothing_Is_Changed()
    {
        var account = await MailboxAsync(1);

        await Assert.ThrowsAsync<UidValidityChangedException>(() => WithSessionAsync(account, async s =>
        {
            var id = (await ListAsync(s))[0].Id;
            return await s.UpdateFlagsAsync(new MessageId(id.Folder, id.UidValidity + 1, id.Uid), new MessageFlagChange(true, null), Token);
        }));

        var item = await WithSessionAsync(account, async s => (await ListAsync(s))[0]);
        Assert.False(item.Seen);
    }

    [Fact]
    public async Task A_Missing_Folder_Is_Reported_As_Not_Found()
    {
        var account = await MailboxAsync(1);

        await Assert.ThrowsAsync<MailFolderNotFoundException>(() =>
            WithSessionAsync(account, s => s.UpdateFlagsAsync(new MessageId("No such folder", 1, 1), new MessageFlagChange(true, null), Token)));
    }

    [Fact]
    public async Task A_Message_In_A_Cyrillic_Folder_Can_Be_Marked()
    {
        var account = await MailboxAsync(0);
        await dovecot.CreateFolderAsync(account, "Работа", Token);
        await dovecot.SaveFixtureAsync(account, "Работа", "02-utf8-qp-alternative.eml", Token);

        var state = await WithSessionAsync(account, async s =>
        {
            var page = await s.ListMessagesAsync(new MessageListQuery("Работа", null, 5), Token);
            return await s.UpdateFlagsAsync(page.Items[0].Id, new MessageFlagChange(true, true), Token);
        });

        Assert.True(state.Seen);
        Assert.True(state.Flagged);
    }
}
