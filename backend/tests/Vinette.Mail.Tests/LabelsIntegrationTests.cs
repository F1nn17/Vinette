using Vinette.Core;
using Vinette.Core.Mail;
using Vinette.TestSupport;

namespace Vinette.Mail.Tests;

/// <summary>Labels as IMAP keywords on a real Dovecot: put on, taken off, renamed and removed everywhere, names of any kind.</summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class LabelsIntegrationTests(DovecotFixture dovecot)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<MailAccount> MailboxAsync(int inbox, params string[] folders)
    {
        var account = await dovecot.CreateAccountAsync($"lbl{Guid.NewGuid():N}"[..14], Token);
        for (var i = 0; i < inbox; i++)
        {
            await dovecot.SaveFixtureAsync(account, "INBOX", i % 2 == 0 ? "01-plain-ascii.eml" : "02-utf8-qp-alternative.eml", Token);
        }

        foreach (var folder in folders)
        {
            await dovecot.CreateFolderAsync(account, folder, Token);
        }

        return account;
    }

    private async Task<T> WithSessionAsync<T>(MailAccount account, Func<IMailSession, Task<T>> action)
    {
        var factory = new ImapMailSessionFactory(new ImapConnectionSettings(dovecot.Host, dovecot.ImapPort, ConnectionSecurity.None, TimeSpan.FromSeconds(30)));
        await using var session = await factory.OpenAsync(new MailCredentials("s", account.Address, account.Password), Token);
        return await action(session);
    }

    private static async Task<IReadOnlyList<MessageListItem>> ListAsync(IMailSession session, string folder) =>
        (await session.ListMessagesAsync(new MessageListQuery(folder, null, 50, NewestFirst: false), Token)).Items;

    [Theory]
    [InlineData("Work")]
    [InlineData("Работа")]
    [InlineData("Счета 2026")]
    [InlineData("Café, 日本")]
    public async Task A_Label_Of_Any_Name_Is_Put_On_A_Message_And_Read_Back_By_Its_Name(string name)
    {
        var account = await MailboxAsync(1);

        var (after, listed, known) = await WithSessionAsync(account, async s =>
        {
            var id = (await ListAsync(s, "INBOX"))[0].Id;
            var labels = await s.UpdateMessageLabelsAsync(id, [name], [], Token);
            return (labels, (await ListAsync(s, "INBOX"))[0].Labels, await s.GetLabelsAsync(Token));
        });

        Assert.Equal([name], after);
        Assert.Equal([name], listed);
        Assert.Contains(name, known);
    }

    [Fact]
    public async Task A_Label_Is_Taken_Off_And_Putting_On_What_Is_There_Is_Not_An_Error()
    {
        var account = await MailboxAsync(1);

        var (twice, none, again) = await WithSessionAsync(account, async s =>
        {
            var id = (await ListAsync(s, "INBOX"))[0].Id;
            await s.UpdateMessageLabelsAsync(id, ["Work", "Личное"], [], Token);
            var a = await s.UpdateMessageLabelsAsync(id, ["Work"], [], Token);
            var b = await s.UpdateMessageLabelsAsync(id, [], ["Work", "Личное"], Token);
            var c = await s.UpdateMessageLabelsAsync(id, [], ["Work"], Token);
            return (a, b, c);
        });

        Assert.Equal(["Work", "Личное"], twice.Order(StringComparer.Ordinal));
        Assert.Empty(none);
        Assert.Empty(again);
    }

    [Fact]
    public async Task A_Label_Put_On_By_Another_Program_Is_Found()
    {
        var account = await MailboxAsync(1);
        await dovecot.DoveadmAsync(Token, "flags", "add", "-u", account.Address, "FromElsewhere", "mailbox", "INBOX", "uid", "1");

        var (known, listed) = await WithSessionAsync(account, async s => (await s.GetLabelsAsync(Token), (await ListAsync(s, "INBOX"))[0].Labels));

        Assert.Contains("FromElsewhere", known);
        Assert.Equal(["FromElsewhere"], listed);
    }

    [Fact]
    public async Task A_Label_No_Message_Carries_Any_More_Is_Not_Listed()
    {
        var account = await MailboxAsync(1);

        var (before, after) = await WithSessionAsync(account, async s =>
        {
            var id = (await ListAsync(s, "INBOX"))[0].Id;
            await s.UpdateMessageLabelsAsync(id, ["Gone"], [], Token);
            var listed = await s.GetLabelsAsync(Token);
            await s.UpdateMessageLabelsAsync(id, [], ["Gone"], Token);
            return (listed, await s.GetLabelsAsync(Token)); // the server still remembers the keyword
        });

        Assert.Contains("Gone", before);
        Assert.DoesNotContain("Gone", after);
    }

    [Fact]
    public async Task System_Flags_Are_Not_Labels()
    {
        var account = await MailboxAsync(1);

        var (labels, known) = await WithSessionAsync(account, async s =>
        {
            var id = (await ListAsync(s, "INBOX"))[0].Id;
            await s.UpdateFlagsAsync(id, new MessageFlagChange(true, true), Token);
            return ((await ListAsync(s, "INBOX"))[0].Labels, await s.GetLabelsAsync(Token));
        });

        Assert.Empty(labels);
        Assert.DoesNotContain(known, k => k.StartsWith('\\') || k.StartsWith('$'));
    }

    [Theory]
    [InlineData("Work", "Job")]
    [InlineData("Work", "Рабочие дела")]
    [InlineData("Работа", "Job")]
    public async Task A_Label_Is_Renamed_On_Every_Message_In_Every_Folder(string from, string to)
    {
        var account = await MailboxAsync(2, "Archive");
        await dovecot.SaveFixtureAsync(account, "Archive", "01-plain-ascii.eml", Token);

        var (inbox, archive, known, changed) = await WithSessionAsync(account, async s =>
        {
            var ids = (await ListAsync(s, "INBOX")).Select(m => m.Id).ToList();
            await s.UpdateMessageLabelsAsync(ids[0], [from], [], Token);
            await s.UpdateMessageLabelsAsync(ids[1], [from, "Other"], [], Token);
            await s.UpdateMessageLabelsAsync((await ListAsync(s, "Archive"))[0].Id, [from], [], Token);
            var count = await s.RenameLabelAsync(from, to, Token);
            return (await ListAsync(s, "INBOX"), await ListAsync(s, "Archive"), await s.GetLabelsAsync(Token), count);
        });

        Assert.Equal(3, changed);
        Assert.Equal([to], inbox[0].Labels);
        Assert.Equal(new[] { "Other", to }.Order(StringComparer.Ordinal), inbox[1].Labels);
        Assert.Equal([to], archive[0].Labels);
        Assert.Contains(to, known);
    }

    [Fact]
    public async Task A_Label_Is_Removed_From_Every_Message_And_Others_Stay()
    {
        var account = await MailboxAsync(2);

        var (inbox, changed) = await WithSessionAsync(account, async s =>
        {
            var ids = (await ListAsync(s, "INBOX")).Select(m => m.Id).ToList();
            await s.UpdateMessageLabelsAsync(ids[0], ["Work", "Keep"], [], Token);
            await s.UpdateMessageLabelsAsync(ids[1], ["Work"], [], Token);
            var count = await s.RenameLabelAsync("Work", null, Token);
            return (await ListAsync(s, "INBOX"), count);
        });

        Assert.Equal(2, changed);
        Assert.Equal(["Keep"], inbox[0].Labels);
        Assert.Empty(inbox[1].Labels);
    }

    [Fact]
    public async Task Renaming_A_Label_That_No_Message_Has_Changes_Nothing()
    {
        var account = await MailboxAsync(1);

        var changed = await WithSessionAsync(account, s => s.RenameLabelAsync("Nobody", "Somebody", Token));

        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task A_Message_That_Is_Gone_Or_Of_An_Old_Folder_Is_Refused()
    {
        var account = await MailboxAsync(1);

        var (gone, stale) = await WithSessionAsync(account, async s =>
        {
            var id = (await ListAsync(s, "INBOX"))[0].Id;
            return (
                await Record.ExceptionAsync(() => s.UpdateMessageLabelsAsync(new MessageId(id.Folder, id.UidValidity, id.Uid + 1000), ["Work"], [], Token)),
                await Record.ExceptionAsync(() => s.UpdateMessageLabelsAsync(new MessageId(id.Folder, id.UidValidity + 1, id.Uid), ["Work"], [], Token)));
        });

        Assert.IsType<MailMessageNotFoundException>(gone);
        Assert.IsType<UidValidityChangedException>(stale);
    }

    [Fact]
    public async Task A_Moved_Message_Keeps_Its_Labels()
    {
        var account = await MailboxAsync(1, "Work");

        var moved = await WithSessionAsync(account, async s =>
        {
            var id = (await ListAsync(s, "INBOX"))[0].Id;
            await s.UpdateMessageLabelsAsync(id, ["Счета 2026"], [], Token);
            _ = await s.MoveMessageAsync(id, "Work", Token);
            return (await ListAsync(s, "Work"))[0].Labels;
        });

        Assert.Equal(["Счета 2026"], moved);
    }

    [Fact]
    public async Task Messages_With_A_Label_Are_Found_In_Every_Folder_Except_The_Trash_And_Spam()
    {
        var account = await MailboxAsync(3, "Work");

        var (found, none) = await WithSessionAsync(account, async s =>
        {
            var ids = (await ListAsync(s, "INBOX")).Select(m => m.Id).ToList();
            foreach (var id in ids)
            {
                await s.UpdateMessageLabelsAsync(id, ["Счета 2026"], [], Token);
            }

            _ = await s.MoveMessageAsync(ids[0], "Work", Token);
            _ = await s.MoveMessageAsync(ids[1], "Trash", Token);
            return (await s.ListLabeledMessagesAsync("Счета 2026", 50, includePreview: false, Token), await s.ListLabeledMessagesAsync("Нет такой", 50, includePreview: false, Token));
        });

        Assert.Equal(2, found.Total);
        Assert.Equal(["INBOX", "Work"], found.Items.Select(i => i.Id.Folder).Order(StringComparer.Ordinal));
        Assert.All(found.Items, i => Assert.Equal(["Счета 2026"], i.Labels));
        Assert.Empty(none.Items);
        Assert.Equal(found.Items.OrderByDescending(i => i.Date).Select(i => i.Id), found.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Only_The_Newest_Messages_Are_Listed_And_The_Total_Counts_All()
    {
        var account = await MailboxAsync(3);

        var found = await WithSessionAsync(account, async s =>
        {
            foreach (var item in await ListAsync(s, "INBOX"))
            {
                await s.UpdateMessageLabelsAsync(item.Id, ["Work"], [], Token);
            }

            return await s.ListLabeledMessagesAsync("Work", 2, includePreview: true, Token);
        });

        Assert.Equal(2, found.Items.Count);
        Assert.Equal(3, found.Total);
        Assert.All(found.Items, i => Assert.NotEmpty(i.Preview));
    }

    [Fact]
    public async Task A_Label_Nobody_Has_Gives_An_Empty_List()
    {
        var account = await MailboxAsync(1);

        var found = await WithSessionAsync(account, s => s.ListLabeledMessagesAsync("Nobody", 50, includePreview: false, Token));

        Assert.Empty(found.Items);
        Assert.Equal(0, found.Total);
    }}
