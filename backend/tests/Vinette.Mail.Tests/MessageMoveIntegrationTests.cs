using Vinette.Core;
using Vinette.Core.Mail;
using Vinette.TestSupport;

namespace Vinette.Mail.Tests;

/// <summary>
/// Moving and deleting messages on a real Dovecot, by both paths: IMAP MOVE, and COPY + mark + expunge for servers
/// without it (forced here by switching the extension off).
/// </summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class MessageMoveIntegrationTests(DovecotFixture dovecot)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<MailAccount> MailboxAsync(int count, params string[] folders)
    {
        var account = await dovecot.CreateAccountAsync($"mov{Guid.NewGuid():N}"[..14], Token);
        for (var i = 0; i < count; i++)
        {
            await dovecot.SaveFixtureAsync(account, "INBOX", i % 2 == 0 ? "01-plain-ascii.eml" : "02-utf8-qp-alternative.eml", Token);
        }

        foreach (var folder in folders)
        {
            await dovecot.CreateFolderAsync(account, folder, Token);
        }

        return account;
    }

    private async Task<T> WithSessionAsync<T>(MailAccount account, bool move, Func<IMailSession, Task<T>> action)
    {
        var factory = new ImapMailSessionFactory(new ImapConnectionSettings(dovecot.Host, dovecot.ImapPort, ConnectionSecurity.None, TimeSpan.FromSeconds(30))) { MoveExtension = move };
        await using var session = await factory.OpenAsync(new MailCredentials("s", account.Address, account.Password), Token);
        return await action(session);
    }

    private static async Task<IReadOnlyList<MessageListItem>> ListAsync(IMailSession session, string folder) =>
        (await session.ListMessagesAsync(new MessageListQuery(folder, null, 50, NewestFirst: false), Token)).Items;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_Message_Moves_To_Another_Folder_With_Its_Marks_And_Is_Found_There_By_The_New_Id(bool move)
    {
        var account = await MailboxAsync(2, "Work");

        var (inbox, work, fetched, newId) = await WithSessionAsync(account, move, async s =>
        {
            var id = (await ListAsync(s, "INBOX"))[0].Id;
            await s.UpdateFlagsAsync(id, new MessageFlagChange(true, true), Token);
            var moved = await s.MoveMessageAsync(id, "Work", Token);
            var source = moved is { } m ? await s.GetMessageAsync(m, Token) : null;
            return (await ListAsync(s, "INBOX"), await ListAsync(s, "Work"), source, moved);
        });

        Assert.Single(inbox); // the other message stays
        Assert.Single(work);
        Assert.NotNull(newId);
        Assert.Equal("Work", newId.Value.Folder);
        Assert.Equal(work[0].Id, newId.Value);
        Assert.NotNull(fetched);
        Assert.True(fetched.Seen);
        Assert.True(fetched.Flagged);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_Message_Moves_Back_To_The_Inbox(bool move)
    {
        var account = await MailboxAsync(1, "Work");

        var (inbox, work) = await WithSessionAsync(account, move, async s =>
        {
            var there = await s.MoveMessageAsync((await ListAsync(s, "INBOX"))[0].Id, "Work", Token);
            _ = await s.MoveMessageAsync(there!.Value, "INBOX", Token);
            return (await ListAsync(s, "INBOX"), await ListAsync(s, "Work"));
        });

        Assert.Single(inbox);
        Assert.Empty(work);
    }

    [Fact]
    public async Task Without_The_Move_Extension_Only_The_Moved_Message_Leaves_Even_If_Another_Is_Marked_Deleted()
    {
        var account = await MailboxAsync(3, "Work");
        var ids = await WithSessionAsync(account, true, async s => (await ListAsync(s, "INBOX")).Select(m => m.Id).ToList());
        // Another message is marked \Deleted by somebody (another client): a plain EXPUNGE would remove it too.
        await dovecot.DoveadmAsync(Token, "flags", "add", "-u", account.Address, "\\Deleted", "mailbox", "INBOX", "uid", ids[1].Uid.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var (inbox, work) = await WithSessionAsync(account, false, async s =>
        {
            _ = await s.MoveMessageAsync(ids[0], "Work", Token);
            return (await ListAsync(s, "INBOX"), await ListAsync(s, "Work"));
        });

        Assert.Equal(2, inbox.Count); // the one marked deleted by the other client is still here
        Assert.Contains(inbox, m => m.Id.Uid == ids[1].Uid);
        Assert.Single(work);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_Missing_Target_Folder_Changes_Nothing(bool move)
    {
        var account = await MailboxAsync(1);

        var (error, inbox) = await WithSessionAsync(account, move, async s =>
        {
            var id = (await ListAsync(s, "INBOX"))[0].Id;
            var thrown = await Record.ExceptionAsync(() => s.MoveMessageAsync(id, "No/Such/Folder", Token));
            return (thrown, await ListAsync(s, "INBOX"));
        });

        Assert.IsType<MailFolderNotFoundException>(error);
        Assert.Single(inbox);
    }

    [Fact]
    public async Task A_Message_That_Is_Gone_Or_Of_An_Old_Folder_Is_Refused()
    {
        var account = await MailboxAsync(1, "Work");

        var (gone, stale) = await WithSessionAsync(account, true, async s =>
        {
            var id = (await ListAsync(s, "INBOX"))[0].Id;
            var missing = new MessageId(id.Folder, id.UidValidity, id.Uid + 1000);
            var old = new MessageId(id.Folder, id.UidValidity + 1, id.Uid);
            return (await Record.ExceptionAsync(() => s.MoveMessageAsync(missing, "Work", Token)), await Record.ExceptionAsync(() => s.MoveMessageAsync(old, "Work", Token)));
        });

        Assert.IsType<MailMessageNotFoundException>(gone);
        Assert.IsType<UidValidityChangedException>(stale);
    }

    [Fact]
    public async Task A_Message_Is_Deleted_For_Good_And_The_Others_Stay()
    {
        var account = await MailboxAsync(0, "Trash");
        await dovecot.SaveFixtureAsync(account, "Trash", "01-plain-ascii.eml", Token);
        await dovecot.SaveFixtureAsync(account, "Trash", "02-utf8-qp-alternative.eml", Token);
        await dovecot.DoveadmAsync(Token, "flags", "add", "-u", account.Address, "\\Deleted", "mailbox", "Trash", "uid", "2"); // another client's mark

        var (before, after) = await WithSessionAsync(account, true, async s =>
        {
            var all = await ListAsync(s, "Trash");
            await s.DeleteMessagePermanentlyAsync(all[0].Id, Token);
            return (all, await ListAsync(s, "Trash"));
        });

        Assert.Equal(2, before.Count);
        Assert.Equal(before[1].Id, Assert.Single(after).Id);
    }

    [Fact]
    public async Task Deleting_A_Message_That_Is_Gone_Is_Refused()
    {
        var account = await MailboxAsync(1);

        var error = await WithSessionAsync(account, true, async s =>
        {
            var id = (await ListAsync(s, "INBOX"))[0].Id;
            return await Record.ExceptionAsync(() => s.DeleteMessagePermanentlyAsync(new MessageId(id.Folder, id.UidValidity, id.Uid + 1000), Token));
        });

        Assert.IsType<MailMessageNotFoundException>(error);
    }
}
