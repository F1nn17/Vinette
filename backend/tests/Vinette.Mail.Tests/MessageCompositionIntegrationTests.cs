using System.Text;
using MimeKit;
using Vinette.Core;
using Vinette.Core.Mail;
using Vinette.TestSupport;

namespace Vinette.Mail.Tests;

/// <summary>
/// The whole way of a written message (VIN-091): built by the composer, handed in to a real submission service, delivered, taken
/// out of the recipient's mailbox through IMAP and taken apart by the application's own parser.
/// </summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class MessageCompositionIntegrationTests(DovecotFixture dovecot)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<MailAccount> AccountAsync(string kind) =>
        await dovecot.CreateAccountAsync($"{kind}{Guid.NewGuid():N}"[..14], Token);

    private static MailCredentials Credentials(MailAccount account) => new("s", account.Address, account.Password);

    private async Task SendAsync(MailAccount from, OutgoingMessage message)
    {
        var composed = new MimeMessageComposer().Compose(message);
        var sender = new SmtpMailSender(new SmtpConnectionSettings(dovecot.Host, dovecot.SubmissionPort, ConnectionSecurity.StartTls, TimeSpan.FromSeconds(30), AllowInvalidCertificate: true));
        await sender.SendAsync(Credentials(from), composed.Envelope, composed.Raw, Token);
    }

    /// <summary>The first message of a mailbox's inbox, once it has arrived, as the mail server holds it.</summary>
    private async Task<MessageSource> ReceiveAsync(MailAccount account, int expectedCount = 1)
    {
        var factory = new ImapMailSessionFactory(new ImapConnectionSettings(dovecot.Host, dovecot.ImapPort, ConnectionSecurity.None, TimeSpan.FromSeconds(30)));
        await using var session = await factory.OpenAsync(Credentials(account), Token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        while (true)
        {
            var page = await session.ListMessagesAsync(new MessageListQuery("INBOX", null, 50, NewestFirst: false), timeout.Token);
            if (page.Items.Count >= expectedCount)
            {
                return await session.GetMessageAsync(page.Items[0].Id, timeout.Token);
            }

            await Task.Delay(300, timeout.Token);
        }
    }

    private static async Task<ParsedMessage> ParseAsync(MessageSource source)
    {
        using var stream = new MemoryStream(source.Content);
        return await new MimeMessageParser().ParseAsync(stream, Token);
    }

    private static MimeMessage Load(MessageSource source)
    {
        using var stream = new MemoryStream(source.Content);
        return MimeMessage.Load(stream, Token);
    }

    [Fact]
    public async Task A_Full_Message_Arrives_As_It_Was_Written()
    {
        var alice = await AccountAsync("snd");
        var bob = await AccountAsync("rcv");
        var carol = await AccountAsync("cop");
        var bytes = Enumerable.Range(0, 20_000).Select(i => (byte)(i % 253)).ToArray();
        var message = new OutgoingMessage(
            new MailAddressInfo("Алиса Соколова", alice.Address),
            [new MailAddressInfo("Боб Иванов", bob.Address)],
            [],
            [new MailAddressInfo("", carol.Address)],
            "Re: Отчёт за ноябрь — итоги",
            "Добрый день!\nОтчёт и таблица во вложении.\n\nС уважением, Алиса",
            "<p>Добрый день!</p><p>Отчёт и таблица <b>во вложении</b>.</p>",
            [
                new OutgoingAttachment("Отчёт за ноябрь №5.pdf", "application/pdf", bytes),
                new OutgoingAttachment("table.csv", "text/csv", Encoding.UTF8.GetBytes("a;b\n1;2\n")),
            ],
            InReplyTo: "<their-last@example.org>",
            References: ["<their-first@example.org>", "<their-last@example.org>"]);

        await SendAsync(alice, message);

        var received = await ReceiveAsync(bob);
        var parsed = await ParseAsync(received);
        Assert.Equal("Re: Отчёт за ноябрь — итоги", parsed.Subject);
        Assert.Equal("Алиса Соколова", parsed.From?.Name);
        Assert.Equal(alice.Address, parsed.From?.Address);
        Assert.Equal("Боб Иванов", Assert.Single(parsed.To).Name);
        Assert.Contains("Отчёт и таблица во вложении.", parsed.Text, StringComparison.Ordinal);
        Assert.Contains("<b>во вложении</b>", parsed.Html, StringComparison.Ordinal);
        Assert.Equal(["Отчёт за ноябрь №5.pdf", "table.csv"], parsed.Attachments.Select(a => a.FileName));
        using var stream = new MemoryStream(received.Content);
        var pdf = await new MimeMessageParser().ExtractAttachmentAsync(stream, parsed.Attachments[0].PartId, Token);
        Assert.Equal(bytes, pdf?.Content);

        var headers = Load(received);
        Assert.Equal("their-last@example.org", headers.InReplyTo);
        Assert.Equal(["their-first@example.org", "their-last@example.org"], headers.References);
        Assert.NotNull(headers.MessageId);
        Assert.DoesNotContain(carol.Address, Encoding.UTF8.GetString(received.Content), StringComparison.Ordinal); // the hidden copy is not named

        var copy = await ParseAsync(await ReceiveAsync(carol));
        Assert.Equal(parsed.Subject, copy.Subject); // and carol did get it
    }

    [Fact]
    public async Task A_Subject_That_Tries_To_Add_A_Recipient_Adds_Nobody()
    {
        var alice = await AccountAsync("snd");
        var bob = await AccountAsync("rcv");
        var victim = await AccountAsync("vic");
        var message = new OutgoingMessage(
            new MailAddressInfo("", alice.Address),
            [new MailAddressInfo("", bob.Address)],
            [],
            [],
            $"Hello\r\nBcc: {victim.Address}",
            "text",
            null,
            []);

        await SendAsync(alice, message);

        var received = await ReceiveAsync(bob);
        Assert.False(Load(received).Headers.Contains("Bcc"));
        await Task.Delay(3000, Token); // time enough for a wrong delivery to have happened
        var factory = new ImapMailSessionFactory(new ImapConnectionSettings(dovecot.Host, dovecot.ImapPort, ConnectionSecurity.None, TimeSpan.FromSeconds(30)));
        await using var session = await factory.OpenAsync(Credentials(victim), Token);
        Assert.Empty((await session.ListMessagesAsync(new MessageListQuery("INBOX", null, 50), Token)).Items);
    }
}
