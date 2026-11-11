using Vinette.Core;
using Vinette.Core.Mail;
using Vinette.TestSupport;

namespace Vinette.Mail.Tests;

/// <summary>The message list against a real Dovecot: pages, cursors, new and deleted mail, flags and fields.</summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class MessageListIntegrationTests(DovecotFixture dovecot)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly string[] Fixtures =
    [
        "01-plain-ascii.eml", "02-utf8-qp-alternative.eml", "03-koi8r-8bit.eml", "04-windows1251-qp.eml", "05-utf8-base64.eml",
        "06-nested-multipart.eml", "07-inline-image-cid.eml", "08-attachments-unicode-names.eml", "09-attached-rfc822.eml", "10-malicious-html.eml",
        "11-dangerous-attachments.eml", "12-external-resources.eml",
    ];

    /// <summary>A mailbox of its own with <paramref name="count"/> messages in INBOX (UIDs 1..count).</summary>
    private async Task<MailAccount> MailboxAsync(int count)
    {
        var account = await dovecot.CreateAccountAsync($"list{Guid.NewGuid():N}"[..14], Token);
        for (var i = 0; i < count; i++)
        {
            await dovecot.SaveFixtureAsync(account, "INBOX", Fixtures[i % Fixtures.Length], Token);
        }

        return account;
    }

    private async Task<T> WithSessionAsync<T>(MailAccount account, Func<IMailSession, Task<T>> action)
    {
        var factory = new ImapMailSessionFactory(new ImapConnectionSettings(dovecot.Host, dovecot.ImapPort, ConnectionSecurity.None, TimeSpan.FromSeconds(30)));
        await using var session = await factory.OpenAsync(new MailCredentials("s", account.Address, account.Password), Token);
        return await action(session);
    }

    private static uint[] Uids(MessagePage page) => [.. page.Items.Select(i => i.Id.Uid)];

    [Fact]
    public async Task The_First_Page_Holds_The_Newest_Messages_And_Points_At_The_Next()
    {
        var account = await MailboxAsync(12);

        var page = await WithSessionAsync(account, s => s.ListMessagesAsync(new MessageListQuery("INBOX", null, 5), Token));

        Assert.Equal([12u, 11, 10, 9, 8], Uids(page));
        Assert.Equal(12, page.Total);
        Assert.Equal(new MessageCursor(page.UidValidity, 8), page.Next);
        Assert.All(page.Items, i => Assert.Equal("INBOX", i.Id.Folder));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Walking_The_Pages_Gives_Every_Message_Once_In_Order(bool newestFirst)
    {
        var account = await MailboxAsync(12);

        var seen = await WithSessionAsync(account, async s =>
        {
            var uids = new List<uint>();
            MessageCursor? cursor = null;
            var pages = 0;
            do
            {
                var page = await s.ListMessagesAsync(new MessageListQuery("INBOX", cursor, 5, newestFirst), Token);
                uids.AddRange(Uids(page));
                cursor = page.Next;
                pages++;
            }
            while (cursor is not null && pages < 10);

            Assert.Equal(3, pages); // 5 + 5 + 2, and no empty page behind the last one
            return uids;
        });

        var expected = Enumerable.Range(1, 12).Select(i => (uint)i);
        Assert.Equal(newestFirst ? expected.Reverse() : expected, seen);
    }

    [Fact]
    public async Task The_Last_Page_Has_No_Next_Even_When_It_Is_Exactly_Full()
    {
        var account = await MailboxAsync(10);

        var last = await WithSessionAsync(account, async s =>
        {
            var first = await s.ListMessagesAsync(new MessageListQuery("INBOX", null, 5), Token);
            return await s.ListMessagesAsync(new MessageListQuery("INBOX", first.Next, 5), Token);
        });

        Assert.Equal([5u, 4, 3, 2, 1], Uids(last));
        Assert.Null(last.Next);
    }

    [Fact]
    public async Task New_Mail_Between_Two_Pages_Does_Not_Shift_The_Next_Page()
    {
        var account = await MailboxAsync(12);

        var (second, secondAfterNewMail) = await WithSessionAsync(account, async s =>
        {
            var first = await s.ListMessagesAsync(new MessageListQuery("INBOX", null, 5), Token);
            var plain = await s.ListMessagesAsync(new MessageListQuery("INBOX", first.Next, 5), Token);
            await dovecot.SaveFixtureAsync(account, "INBOX", "01-plain-ascii.eml", Token);
            await dovecot.SaveFixtureAsync(account, "INBOX", "02-utf8-qp-alternative.eml", Token);
            var afterNew = await s.ListMessagesAsync(new MessageListQuery("INBOX", first.Next, 5), Token);
            return (plain, afterNew);
        });

        Assert.Equal([7u, 6, 5, 4, 3], Uids(second));
        Assert.Equal(Uids(second), Uids(secondAfterNewMail));
        Assert.Equal(14, secondAfterNewMail.Total);
    }

    [Fact]
    public async Task A_Message_Deleted_At_The_Edge_Of_A_Page_Neither_Breaks_The_Next_Page_Nor_Repeats_Anything()
    {
        var account = await MailboxAsync(12);

        var (first, second) = await WithSessionAsync(account, async s =>
        {
            var one = await s.ListMessagesAsync(new MessageListQuery("INBOX", null, 5), Token); // 12..8
            await dovecot.DoveadmAsync(Token, "expunge", "-u", account.Address, "mailbox", "INBOX", "uid", "8"); // the cursor message itself
            await dovecot.DoveadmAsync(Token, "expunge", "-u", account.Address, "mailbox", "INBOX", "uid", "6");
            var two = await s.ListMessagesAsync(new MessageListQuery("INBOX", one.Next, 5), Token);
            return (one, two);
        });

        Assert.Equal([12u, 11, 10, 9, 8], Uids(first));
        Assert.Equal([7u, 5, 4, 3, 2], Uids(second)); // 8 and 6 are gone, nothing before 8 is repeated
        Assert.Equal(10, second.Total);
    }

    [Fact]
    public async Task A_Cursor_Of_A_Recreated_Folder_Is_Refused()
    {
        var account = await MailboxAsync(3);
        var page = await WithSessionAsync(account, s => s.ListMessagesAsync(new MessageListQuery("INBOX", null, 2), Token));
        var stale = new MessageCursor(page.UidValidity + 1, page.Next!.Value.Uid);

        var error = await Assert.ThrowsAsync<UidValidityChangedException>(() =>
            WithSessionAsync(account, s => s.ListMessagesAsync(new MessageListQuery("INBOX", stale, 2), Token)));

        Assert.Equal(page.UidValidity, error.Current);
    }

    [Theory]
    [InlineData("No such folder")]
    [InlineData("Работа.Нет")]
    public async Task A_Missing_Folder_Is_Reported_As_Not_Found(string folder)
    {
        var account = await MailboxAsync(1);

        await Assert.ThrowsAsync<MailFolderNotFoundException>(() =>
            WithSessionAsync(account, s => s.ListMessagesAsync(new MessageListQuery(folder, null, 5), Token)));
    }

    [Fact]
    public async Task An_Empty_Folder_Gives_An_Empty_Page_And_A_Cyrillic_Folder_Works()
    {
        var account = await MailboxAsync(0);
        await dovecot.CreateFolderAsync(account, "Работа", Token);
        await dovecot.SaveFixtureAsync(account, "Работа", "02-utf8-qp-alternative.eml", Token);

        var (inbox, work) = await WithSessionAsync(account, async s =>
            (await s.ListMessagesAsync(new MessageListQuery("INBOX", null, 5), Token),
             await s.ListMessagesAsync(new MessageListQuery("Работа", null, 5), Token)));

        Assert.Empty(inbox.Items);
        Assert.Null(inbox.Next);
        Assert.Equal(0, inbox.Total);
        var item = Assert.Single(work.Items);
        Assert.Equal("Работа", item.Id.Folder);
        Assert.Equal("Привет, мир", item.Subject); // the encoded subject comes decoded
    }

    [Fact]
    public async Task Every_Field_Of_A_Line_Is_Filled_From_The_Message_And_Its_Flags()
    {
        var account = await MailboxAsync(0);
        await dovecot.SaveFixtureAsync(account, "INBOX", "01-plain-ascii.eml", Token); // uid 1: read, flagged, a label
        await dovecot.SaveFixtureAsync(account, "INBOX", "08-attachments-unicode-names.eml", Token); // uid 2: attachments, unread
        await dovecot.DoveadmAsync(Token, "flags", "add", "-u", account.Address, "\\Seen \\Flagged", "mailbox", "INBOX", "uid", "1");
        await dovecot.DoveadmAsync(Token, "flags", "add", "-u", account.Address, "Work", "mailbox", "INBOX", "uid", "1");

        var page = await WithSessionAsync(account, s => s.ListMessagesAsync(new MessageListQuery("INBOX", null, 5, NewestFirst: false), Token));

        var plain = page.Items[0];
        Assert.Equal("Plain ASCII message", plain.Subject);
        Assert.Equal("anna@example.org", plain.From?.Address);
        Assert.True(plain.Seen);
        Assert.True(plain.Flagged);
        Assert.False(plain.Answered);
        Assert.False(plain.Draft);
        Assert.False(plain.HasAttachments);
        Assert.Equal(["Work"], plain.Labels);
        Assert.True(plain.Size > 0);
        Assert.NotEqual(DateTimeOffset.UnixEpoch, plain.Date);
        var withFiles = page.Items[1];
        Assert.True(withFiles.HasAttachments);
        Assert.False(withFiles.Seen);
        Assert.Empty(withFiles.Labels);
    }

    /// <summary>12 messages; the odd UIDs are read, UIDs 3, 4, 9 and 10 are flagged.</summary>
    private async Task<MailAccount> FilteredMailboxAsync()
    {
        var account = await MailboxAsync(12);
        foreach (var uid in new[] { "1", "3", "5", "7", "9", "11" })
        {
            await dovecot.DoveadmAsync(Token, "flags", "add", "-u", account.Address, "\\Seen", "mailbox", "INBOX", "uid", uid);
        }

        foreach (var uid in new[] { "3", "4", "9", "10" })
        {
            await dovecot.DoveadmAsync(Token, "flags", "add", "-u", account.Address, "\\Flagged", "mailbox", "INBOX", "uid", uid);
        }

        return account;
    }

    [Fact]
    public async Task The_Unread_Filter_Pages_Through_Only_Unread_Messages()
    {
        var account = await FilteredMailboxAsync();

        var (first, second, third) = await WithSessionAsync(account, async s =>
        {
            var a = await s.ListMessagesAsync(new MessageListQuery("INBOX", null, 4, Filter: MessageFilter.Unread), Token);
            var b = await s.ListMessagesAsync(new MessageListQuery("INBOX", a.Next, 4, Filter: MessageFilter.Unread), Token);
            return (a, b, b);
        });

        Assert.Equal([12u, 10, 8, 6], Uids(first));
        Assert.All(first.Items, i => Assert.False(i.Seen));
        Assert.Equal(6, first.Total);
        Assert.Equal([4u, 2], Uids(second));
        Assert.Null(third.Next);
    }

    [Fact]
    public async Task The_Flagged_Filter_Works_In_Both_Orders_And_Without_A_Next_On_An_Exact_Page()
    {
        var account = await FilteredMailboxAsync();

        var (newest, oldest, exact) = await WithSessionAsync(account, async s => (
            await s.ListMessagesAsync(new MessageListQuery("INBOX", null, 3, Filter: MessageFilter.Flagged), Token),
            await s.ListMessagesAsync(new MessageListQuery("INBOX", null, 10, NewestFirst: false, Filter: MessageFilter.Flagged), Token),
            await s.ListMessagesAsync(new MessageListQuery("INBOX", null, 4, Filter: MessageFilter.Flagged), Token)));

        Assert.Equal([10u, 9, 4], Uids(newest));
        Assert.Equal(new MessageCursor(newest.UidValidity, 4), newest.Next);
        Assert.Equal([3u, 4, 9, 10], Uids(oldest));
        Assert.Equal(4, oldest.Total);
        Assert.All(oldest.Items, i => Assert.True(i.Flagged));
        Assert.Null(exact.Next);
    }

    [Fact]
    public async Task A_Filter_With_No_Matches_Gives_An_Empty_Page_And_Mail_Read_Meanwhile_Leaves_The_Cursor_Valid()
    {
        var account = await FilteredMailboxAsync();

        var (flaggedOnEmpty, afterRead) = await WithSessionAsync(account, async s =>
        {
            await dovecot.DoveadmAsync(Token, "flags", "remove", "-u", account.Address, "\\Flagged", "mailbox", "INBOX", "all");
            var none = await s.ListMessagesAsync(new MessageListQuery("INBOX", null, 5, Filter: MessageFilter.Flagged), Token);
            var first = await s.ListMessagesAsync(new MessageListQuery("INBOX", null, 2, Filter: MessageFilter.Unread), Token); // 12, 10
            await dovecot.DoveadmAsync(Token, "flags", "add", "-u", account.Address, "\\Seen", "mailbox", "INBOX", "uid", "8");
            var next = await s.ListMessagesAsync(new MessageListQuery("INBOX", first.Next, 2, Filter: MessageFilter.Unread), Token);
            return (none, next);
        });

        Assert.Empty(flaggedOnEmpty.Items);
        Assert.Equal(0, flaggedOnEmpty.Total);
        Assert.Null(flaggedOnEmpty.Next);
        Assert.Equal([6u, 4], Uids(afterRead)); // 8 was read in between and is gone from the list, nothing repeats
    }
}
