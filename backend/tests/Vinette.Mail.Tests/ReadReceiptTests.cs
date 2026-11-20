using System.Text;
using MimeKit;
using Vinette.Core.Mail;

namespace Vinette.Mail.Tests;

/// <summary>Read receipts (VIN-106): the request in an outgoing message, read back from an incoming one, and the receipt itself (RFC 8098).</summary>
public sealed class ReadReceiptTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly MimeMessageComposer Composer = new();

    private static readonly MimeMessageParser Parser = new();

    private static MailAddressInfo Person(string address, string name = "") => new(name, address);

    private static OutgoingMessage Message(bool requestReceipt) =>
        new(Person("me@vinette.test", "Алиса"), [Person("anna@example.org")], [], [], "Hello", "Hi", null, [], RequestReceipt: requestReceipt);

    private static MimeMessage Read(ComposedMessage composed) => MimeMessage.Load(new MemoryStream(composed.Raw.ToArray()), Token);

    [Fact]
    public void A_Message_Asks_For_A_Receipt_Only_When_Told_To_And_Names_Only_Its_Sender()
    {
        var asking = Read(Composer.Compose(Message(requestReceipt: true)));
        var quiet = Read(Composer.Compose(Message(requestReceipt: false)));

        var header = asking.Headers[HeaderId.DispositionNotificationTo];
        Assert.NotNull(header);
        Assert.Contains("me@vinette.test", header, StringComparison.Ordinal);
        Assert.DoesNotContain("anna@example.org", header, StringComparison.Ordinal);
        Assert.Null(quiet.Headers[HeaderId.DispositionNotificationTo]);
    }

    [Fact]
    public void A_Draft_Keeps_The_Request()
    {
        var draft = Read(Composer.ComposeDraft(Message(requestReceipt: true)));

        Assert.NotNull(draft.Headers[HeaderId.DispositionNotificationTo]);
    }

    [Fact]
    public async Task The_Parser_Reads_The_Request_Of_An_Incoming_Message_And_Finds_None_In_One_Without()
    {
        var asking = Encoding.UTF8.GetBytes("From: Anna <anna@example.org>\r\nDisposition-Notification-To: =?utf-8?B?0JDQvdC90LA=?= <anna@example.org>, other@example.org\r\nSubject: s\r\n\r\nbody\r\n");
        var quiet = Encoding.UTF8.GetBytes("From: anna@example.org\r\nSubject: s\r\n\r\nbody\r\n");

        var parsed = await Parser.ParseAsync(new MemoryStream(asking), Token);
        var none = await Parser.ParseAsync(new MemoryStream(quiet), Token);

        Assert.Equal(new MailAddressInfo("Анна", "anna@example.org"), parsed.ReceiptTo);
        Assert.Null(none.ReceiptTo);
    }

    [Fact]
    public async Task What_The_Composer_Asks_Is_What_The_Parser_Finds()
    {
        var composed = Composer.Compose(Message(requestReceipt: true));

        var parsed = await Parser.ParseAsync(new MemoryStream(composed.Raw.ToArray()), Token);

        Assert.Equal("me@vinette.test", parsed.ReceiptTo?.Address);
    }

    [Fact]
    public void The_Receipt_Is_A_Report_To_The_Sender_That_Names_The_Message_That_Was_Displayed()
    {
        var composed = Composer.ComposeReceipt(new ReceiptRequest(
            Person("bob@vinette.test"),
            Person("anna@example.org", "Анна"),
            "orig-1@example.org",
            "Смета",
            new DateTimeOffset(2026, 11, 17, 9, 30, 0, TimeSpan.Zero)));

        var mime = Read(composed);
        var report = Assert.IsType<MultipartReport>(mime.Body);
        Assert.Equal("disposition-notification", report.ReportType);
        Assert.Equal("bob@vinette.test", Assert.Single(mime.From.Mailboxes).Address);
        Assert.Equal("anna@example.org", Assert.Single(mime.To.Mailboxes).Address);
        Assert.Equal("Read: Смета", mime.Subject);
        Assert.Equal("orig-1@example.org", mime.InReplyTo);
        Assert.Contains("orig-1@example.org", mime.References, StringComparer.Ordinal);
        Assert.Null(mime.Headers[HeaderId.DispositionNotificationTo]); // a receipt never asks for a receipt
        var human = report.OfType<TextPart>().Single();
        Assert.Contains("has been displayed", human.Text, StringComparison.Ordinal);
        Assert.Contains("Смета", human.Text, StringComparison.Ordinal);
        Assert.Contains("2026-11-17 09:30 UTC", human.Text, StringComparison.Ordinal);
        var notification = report.OfType<MessageDispositionNotification>().Single();
        Assert.Equal("rfc822; bob@vinette.test", notification.Fields["Final-Recipient"]);
        Assert.Equal("<orig-1@example.org>", notification.Fields["Original-Message-ID"]);
        Assert.Equal("manual-action/MDN-sent-manually; displayed", notification.Fields["Disposition"]);
        Assert.Equal("bob@vinette.test", composed.Envelope.From);
        Assert.Equal(["anna@example.org"], composed.Envelope.Recipients);
    }

    [Fact]
    public void The_Receipt_Is_Safe_On_Any_Relay_And_Takes_Nothing_Typed_For_A_Header()
    {
        var composed = Composer.ComposeReceipt(new ReceiptRequest(
            Person("bob@vinette.test"),
            Person("anna@example.org"),
            null,
            "Hi" + (char)13 + (char)10 + "Bcc: evil@example.org",
            null));

        var text = Encoding.ASCII.GetString(composed.Raw.ToArray()); // seven bit: ASCII reads it all
        var mime = Read(composed);
        Assert.Empty(mime.Bcc);
        Assert.Contains("Read: Hi  Bcc: evil@example.org", mime.Subject, StringComparison.Ordinal); // one line, as text
        Assert.True(text.Length > 0);
    }

    [Fact]
    public void A_Receipt_To_Something_That_Is_Not_An_Address_Is_Refused()
    {
        Assert.Throws<ArgumentException>(() => Composer.ComposeReceipt(new ReceiptRequest(Person("bob@vinette.test"), Person("not an address"), null, "s", null)));
    }

    [Fact]
    public void A_Very_Long_Subject_Is_Cut()
    {
        var composed = Composer.ComposeReceipt(new ReceiptRequest(Person("bob@vinette.test"), Person("anna@example.org"), null, new string('x', 1000), null));

        Assert.True(Read(composed).Subject?.Length <= 210);
    }
}
