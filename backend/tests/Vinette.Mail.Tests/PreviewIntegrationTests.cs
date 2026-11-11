using Vinette.Core;
using Vinette.Core.Mail;
using Vinette.TestSupport;

namespace Vinette.Mail.Tests;

/// <summary>
/// Message previews against a real Dovecot, both ways a preview can come: from the server's PREVIEW extension and, for
/// servers without it, from the beginning of the message body that MailKit downloads and decodes itself.
/// </summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class PreviewIntegrationTests(DovecotFixture dovecot)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly string[] Fixtures =
    [
        "01-plain-ascii.eml", "02-utf8-qp-alternative.eml", "03-koi8r-8bit.eml", "04-windows1251-qp.eml",
        "05-utf8-base64.eml", "13-html-only.eml", "14-attachment-only.eml", "10-malicious-html.eml",
    ];

    private async Task<(IMailSession Session, MailAccount Account)> OpenAsync(bool extension)
    {
        var account = await dovecot.CreateAccountAsync($"prev{Guid.NewGuid():N}"[..14], Token);
        foreach (var file in Fixtures)
        {
            await dovecot.SaveFixtureAsync(account, "INBOX", file, Token);
        }

        var factory = new ImapMailSessionFactory(new ImapConnectionSettings(dovecot.Host, dovecot.ImapPort, ConnectionSecurity.None, TimeSpan.FromSeconds(30))) { PreviewExtension = extension };
        return (await factory.OpenAsync(new MailCredentials("s", account.Address, account.Password), Token), account);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Previews_Are_Readable_Whatever_The_Encoding_Of_The_Message(bool extension)
    {
        var (session, _) = await OpenAsync(extension);
        await using var _ = session;

        var page = await session.ListMessagesAsync(new MessageListQuery("INBOX", null, 20, NewestFirst: false), Token);

        var previews = page.Items.ToDictionary(i => i.Id.Uid, i => i.Preview);
        Assert.Equal("Hello, this is a plain message.", previews[1]);
        Assert.Contains("Привет! Это проверка кодировок", previews[2], StringComparison.Ordinal); // quoted-printable, UTF-8
        Assert.Contains("Здравствуйте! Это письмо в кодировке KOI8-R", previews[3], StringComparison.Ordinal); // raw 8-bit koi8-r
        Assert.Contains("Добрый день! Письмо в кодировке Windows-1251", previews[4], StringComparison.Ordinal); // quoted-printable, windows-1251
        Assert.Contains("Тело письма в base64", previews[5], StringComparison.Ordinal); // base64
        Assert.Contains("Heading HTML-only body", previews[6], StringComparison.Ordinal); // HTML only: text without tags
        Assert.Equal(string.Empty, previews[7]); // nothing to show, and no failure
        Assert.Contains("safe-marker", previews[8], StringComparison.Ordinal);
        Assert.All(previews.Values, p =>
        {
            Assert.DoesNotContain('<', p);
            Assert.DoesNotContain('\n', p);
            Assert.True(p.Length <= PreviewText.MaxLength + 1);
        });
    }

    [Fact]
    public async Task The_Preview_Is_Left_Out_When_Not_Asked_For()
    {
        var (session, _) = await OpenAsync(extension: false);
        await using var _ = session;

        var page = await session.ListMessagesAsync(new MessageListQuery("INBOX", null, 20, IncludePreview: false), Token);

        Assert.Equal(Fixtures.Length, page.Items.Count);
        Assert.All(page.Items, i => Assert.Equal(string.Empty, i.Preview));
    }
}
