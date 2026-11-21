using Vinette.Core;
using Vinette.Core.Mail;
using Vinette.TestSupport;

namespace Vinette.Mail.Tests;

/// <summary>
/// Search against a real Dovecot: Cyrillic words are found in subjects and in texts that came as UTF-8, koi8-r and windows-1251, the words
/// narrow each other, and the page and the filter work on the found messages. The mailbox holds the first twelve test mails (UIDs 1..12):
/// 02 "Привет, мир" (UTF-8), 03 "Тест KOI8-R" (koi8-r), 04 "Отчёт за квартал" (windows-1251), all from anna@example.org.
/// </summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class MessageSearchIntegrationTests(DovecotFixture dovecot)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly string[] Fixtures =
    [
        "01-plain-ascii.eml", "02-utf8-qp-alternative.eml", "03-koi8r-8bit.eml", "04-windows1251-qp.eml", "05-utf8-base64.eml",
        "06-nested-multipart.eml", "07-inline-image-cid.eml", "08-attachments-unicode-names.eml", "09-attached-rfc822.eml", "10-malicious-html.eml",
        "11-dangerous-attachments.eml", "12-external-resources.eml",
    ];

    private async Task<MailAccount> MailboxAsync()
    {
        var account = await dovecot.CreateAccountAsync($"find{Guid.NewGuid():N}"[..14], Token);
        foreach (var fixture in Fixtures)
        {
            await dovecot.SaveFixtureAsync(account, "INBOX", fixture, Token);
        }

        return account;
    }

    private async Task<MessagePage> SearchAsync(
        MailAccount account,
        string text,
        MessageSearchScope scope = MessageSearchScope.All,
        int limit = 50,
        MessageCursor? after = null,
        MessageFilter filter = MessageFilter.All)
    {
        var factory = new ImapMailSessionFactory(new ImapConnectionSettings(dovecot.Host, dovecot.ImapPort, ConnectionSecurity.None, TimeSpan.FromSeconds(30)));
        await using var session = await factory.OpenAsync(new MailCredentials("s", account.Address, account.Password), Token);
        var query = new MessageListQuery("INBOX", after, limit, true, false, filter, MessageSearch.Terms(text), scope);
        return await session.ListMessagesAsync(query, Token);
    }

    private static uint[] Uids(MessagePage page) => [.. page.Items.Select(i => i.Id.Uid)];

    [Fact]
    public async Task A_Cyrillic_Word_Is_Found_In_A_Subject_Without_Regard_To_Case_Or_The_Encoding_Of_The_Subject()
    {
        var account = await MailboxAsync();

        var lower = await SearchAsync(account, "квартал", MessageSearchScope.Subject); // windows-1251, Q-encoded subject
        var upper = await SearchAsync(account, "КВАРТАЛ", MessageSearchScope.Subject);
        var utf8 = await SearchAsync(account, "привет", MessageSearchScope.Subject); // UTF-8, Q-encoded subject
        var koi = await SearchAsync(account, "Тест", MessageSearchScope.Subject); // koi8-r, B-encoded subject

        Assert.Equal([4u], Uids(lower));
        Assert.Equal([4u], Uids(upper));
        Assert.Contains(2u, Uids(utf8));
        Assert.Contains(3u, Uids(koi));
    }

    [Fact]
    public async Task A_Cyrillic_Word_Is_Found_In_The_Text_Whatever_The_Charset_Of_The_Text()
    {
        var account = await MailboxAsync();

        var page = await SearchAsync(account, "кодировке", MessageSearchScope.Body);

        // 03 is koi8-r 8-bit, 04 is windows-1251 quoted-printable; "кодировок" (02, UTF-8) is another word.
        Assert.Equal([4u, 3u], Uids(page));
        Assert.Equal(2, page.Total);
    }

    [Fact]
    public async Task The_Words_Narrow_Each_Other_In_Any_Order_And_Search_All_Looks_In_The_Subject_And_The_Text()
    {
        var account = await MailboxAsync();

        var both = await SearchAsync(account, "KOI8-R Тест", MessageSearchScope.Subject);
        var onlyOne = await SearchAsync(account, "Тест квартал", MessageSearchScope.Subject);
        var everywhere = await SearchAsync(account, "Windows-1251"); // only in the text of 04
        var bySender = await SearchAsync(account, "anna@example.org", MessageSearchScope.From);

        Assert.Equal([3u], Uids(both));
        Assert.Empty(onlyOne.Items);
        Assert.Contains(4u, Uids(everywhere));
        Assert.All(bySender.Items, i => Assert.Equal("anna@example.org", i.From?.Address));
        Assert.True(bySender.Total >= 3);
    }

    [Fact]
    public async Task A_Search_That_Finds_Nothing_Is_An_Empty_Page_And_Odd_Characters_Do_Not_Break_It()
    {
        var account = await MailboxAsync();

        var none = await SearchAsync(account, "нет-такого-слова");
        var odd = await SearchAsync(account, "a\"b\\c (d) {e} * %");

        Assert.Empty(none.Items);
        Assert.Equal(0, none.Total);
        Assert.Null(none.Next);
        Assert.Empty(odd.Items);
    }

    [Fact]
    public async Task The_Found_Messages_Are_Paged_With_The_Cursor_And_The_Filter_Narrows_Them_Further()
    {
        var account = await MailboxAsync();

        var first = await SearchAsync(account, "кодировке", MessageSearchScope.Body, limit: 1);
        var second = await SearchAsync(account, "кодировке", MessageSearchScope.Body, limit: 1, after: first.Next);
        var flagged = await SearchAsync(account, "кодировке", MessageSearchScope.Body, filter: MessageFilter.Flagged);
        var unread = await SearchAsync(account, "кодировке", MessageSearchScope.Body, filter: MessageFilter.Unread);

        Assert.Equal([4u], Uids(first));
        Assert.NotNull(first.Next);
        Assert.Equal(2, first.Total);
        Assert.Equal([3u], Uids(second));
        Assert.Null(second.Next);
        Assert.Empty(flagged.Items); // nothing is flagged, though the words match
        Assert.Equal([4u, 3u], Uids(unread)); // nothing was read
    }
}
