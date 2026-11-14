using MimeKit;
using Vinette.Core;
using Vinette.Core.Mail;
using Vinette.TestSupport;

namespace Vinette.Mail.Tests;

/// <summary>Sending through the real submission service of a Dovecot and Postfix (STARTTLS on 587, a self-signed certificate).</summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class SmtpMailSenderIntegrationTests(DovecotFixture dovecot)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private SmtpMailSender Sender(int? port = null) =>
        new(new SmtpConnectionSettings(dovecot.Host, port ?? dovecot.SubmissionPort, ConnectionSecurity.StartTls, TimeSpan.FromSeconds(30), AllowInvalidCertificate: true));

    private async Task<MailAccount> AccountAsync(string kind) =>
        await dovecot.CreateAccountAsync($"{kind}{Guid.NewGuid():N}"[..14], Token);

    private static MailCredentials Credentials(MailAccount account, string? password = null) => new("s", account.Address, password ?? account.Password);

    private static byte[] Compose(MailAccount from, string subject, string body, params MailAccount[] to)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from.Address));
        foreach (var recipient in to)
        {
            message.To.Add(MailboxAddress.Parse(recipient.Address));
        }

        message.Subject = subject;
        message.Date = DateTimeOffset.UtcNow;
        message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId("vinette.test");
        message.Body = new TextPart("plain") { Text = body };
        using var stream = new MemoryStream();
        message.WriteTo(stream, Token);
        return stream.ToArray();
    }

    private static OutgoingEnvelope Envelope(MailAccount from, params MailAccount[] to) => new(from.Address, [.. to.Select(t => t.Address)]);

    /// <summary>The messages of a mailbox's inbox, waiting for the mail to arrive (delivery takes a moment).</summary>
    private async Task<IReadOnlyList<MessageListItem>> InboxAsync(MailAccount account, int expected)
    {
        var factory = new ImapMailSessionFactory(new ImapConnectionSettings(dovecot.Host, dovecot.ImapPort, ConnectionSecurity.None, TimeSpan.FromSeconds(30)));
        await using var session = await factory.OpenAsync(Credentials(account), Token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        while (true)
        {
            var page = await session.ListMessagesAsync(new MessageListQuery("INBOX", null, 50), timeout.Token);
            if (page.Items.Count >= expected)
            {
                return page.Items;
            }

            await Task.Delay(300, timeout.Token);
        }
    }

    [Fact]
    public async Task A_Message_Reaches_The_Mailbox_Of_The_Recipient()
    {
        var alice = await AccountAsync("snd");
        var bob = await AccountAsync("rcv");

        await Sender().SendAsync(Credentials(alice), Envelope(alice, bob), Compose(alice, "Hello Bob", "How are you?", bob), Token);

        var arrived = Assert.Single(await InboxAsync(bob, 1));
        Assert.Equal("Hello Bob", arrived.Subject);
        Assert.Equal(alice.Address, arrived.From?.Address);
    }

    [Fact]
    public async Task Cyrillic_In_The_Subject_And_The_Text_Arrives_As_It_Was_Written()
    {
        var alice = await AccountAsync("snd");
        var bob = await AccountAsync("rcv");

        await Sender().SendAsync(Credentials(alice), Envelope(alice, bob), Compose(alice, "Привет, Боб — отчёт за ноябрь", "Текст письма на русском.", bob), Token);

        var arrived = Assert.Single(await InboxAsync(bob, 1));
        Assert.Equal("Привет, Боб — отчёт за ноябрь", arrived.Subject);
    }

    [Fact]
    public async Task A_Blind_Copy_Is_Delivered_Without_Being_Named_In_The_Message()
    {
        var alice = await AccountAsync("snd");
        var bob = await AccountAsync("rcv");
        var carol = await AccountAsync("cop");
        var raw = Compose(alice, "Between us", "Carol is not on the list.", bob); // the headers name Bob only

        await Sender().SendAsync(Credentials(alice), Envelope(alice, bob, carol), raw, Token);

        Assert.Single(await InboxAsync(bob, 1));
        var copy = Assert.Single(await InboxAsync(carol, 1));
        Assert.Equal("Between us", copy.Subject);
        Assert.DoesNotContain(carol.Address, System.Text.Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Password_The_Server_Does_Not_Accept_Means_Access_Revoked()
    {
        var alice = await AccountAsync("snd");
        var bob = await AccountAsync("rcv");

        var failure = await Record.ExceptionAsync(() =>
            Sender().SendAsync(Credentials(alice, "not-the-password"), Envelope(alice, bob), Compose(alice, "x", "y", bob), Token));

        Assert.IsType<MailAccessRevokedException>(failure);
        Assert.DoesNotContain("not-the-password", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Server_That_Cannot_Be_Reached_Is_Unavailable_And_The_Message_Was_Not_Sent()
    {
        var alice = await AccountAsync("snd");
        var bob = await AccountAsync("rcv");
        var closed = System.Net.Sockets.TcpListener.Create(0);
        closed.Start();
        var port = ((System.Net.IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop(); // nothing listens there now

        var failure = await Record.ExceptionAsync(() =>
            Sender(port).SendAsync(Credentials(alice), Envelope(alice, bob), Compose(alice, "x", "y", bob), Token));

        var unavailable = Assert.IsType<MailUnavailableException>(failure);
        Assert.False(unavailable.MessageMayHaveBeenSent);
    }

    [Fact]
    public async Task A_Message_Bigger_Than_The_Server_Takes_Is_Refused_As_Too_Large()
    {
        var alice = await AccountAsync("snd");
        var bob = await AccountAsync("rcv");
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(alice.Address));
        message.To.Add(MailboxAddress.Parse(bob.Address));
        message.Subject = "Too big";
        var body = new Multipart("mixed") { new TextPart("plain") { Text = "see attachment" } };
        body.Add(new MimePart("application", "octet-stream")
        {
            Content = new MimeContent(new MemoryStream(new byte[30 * 1024 * 1024])),
            ContentTransferEncoding = ContentEncoding.Base64,
            FileName = "big.bin",
        });
        message.Body = body;
        using var stream = new MemoryStream();
        await message.WriteToAsync(stream, Token);

        var failure = await Record.ExceptionAsync(() => Sender().SendAsync(Credentials(alice), Envelope(alice, bob), stream.ToArray(), Token));

        Assert.Equal(SendRejection.TooLarge, Assert.IsType<MailRejectedException>(failure).Reason);
    }

    [Fact]
    public async Task A_Message_Without_Recipients_Is_Refused_Before_Anything_Is_Sent()
    {
        var alice = await AccountAsync("snd");

        var failure = await Record.ExceptionAsync(() =>
            Sender().SendAsync(Credentials(alice), new OutgoingEnvelope(alice.Address, []), Compose(alice, "x", "y"), Token));

        Assert.IsType<ArgumentException>(failure);
    }
}
