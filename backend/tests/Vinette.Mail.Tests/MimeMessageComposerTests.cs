using System.Text;
using MimeKit;
using Vinette.Core.Mail;

namespace Vinette.Mail.Tests;

/// <summary>
/// The mail the composer builds, read back by the application's own parser and by MimeKit (VIN-091): what the user wrote comes out as
/// it was written, nothing the user typed can become a header, and the mail is safe on any relay. No mail server needed.
/// </summary>
public sealed class MimeMessageComposerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly MimeMessageComposer Composer = new();

    private static MailAddressInfo Person(string address, string name = "") => new(name, address);

    private static OutgoingMessage Message(
        string subject = "Hello",
        string? text = "Hi there",
        string? html = null,
        IReadOnlyList<OutgoingAttachment>? attachments = null,
        IReadOnlyList<MailAddressInfo>? to = null,
        IReadOnlyList<MailAddressInfo>? cc = null,
        IReadOnlyList<MailAddressInfo>? bcc = null,
        string? inReplyTo = null,
        IReadOnlyList<string>? references = null,
        string? messageId = null,
        DateTimeOffset? date = null) =>
        new(
            Person("me@vinette.test", "Me"),
            to ?? [Person("anna@example.org", "Anna")],
            cc ?? [],
            bcc ?? [],
            subject,
            text,
            html,
            attachments ?? [],
            inReplyTo,
            references,
            messageId,
            date);

    private static OutgoingAttachment File(string name, byte[] bytes, string type = "application/pdf") => new(name, type, bytes);

    private static async Task<ParsedMessage> ParseAsync(ComposedMessage composed)
    {
        using var stream = new MemoryStream(composed.Raw.ToArray());
        return await new MimeMessageParser().ParseAsync(stream, Token);
    }

    private static MimeMessage Load(ComposedMessage composed)
    {
        using var stream = new MemoryStream(composed.Raw.ToArray());
        return MimeMessage.Load(stream, Token);
    }

    private static string Wire(ComposedMessage composed) => Encoding.ASCII.GetString(composed.Raw.Span);

    [Fact]
    public async Task A_Plain_Text_Message_Is_One_Text_Part_And_Reads_Back_As_Written()
    {
        var composed = Composer.Compose(Message(subject: "Weekly report", text: "Line one\nLine two"));

        var parsed = await ParseAsync(composed);

        Assert.Equal("Weekly report", parsed.Subject);
        Assert.Equal("me@vinette.test", parsed.From?.Address);
        Assert.Equal("Me", parsed.From?.Name);
        Assert.Equal("anna@example.org", Assert.Single(parsed.To).Address);
        Assert.Contains("Line one", parsed.Text, StringComparison.Ordinal);
        Assert.Contains("Line two", parsed.Text, StringComparison.Ordinal);
        Assert.Null(parsed.Html);
        Assert.IsType<TextPart>(Load(composed).Body);
    }

    [Fact]
    public async Task Text_And_Html_Are_Alternatives_In_That_Order()
    {
        var composed = Composer.Compose(Message(text: "Plain words", html: "<p>Rich <b>words</b></p>"));

        var parsed = await ParseAsync(composed);

        var body = Assert.IsAssignableFrom<Multipart>(Load(composed).Body);
        Assert.Equal("alternative", body.ContentType.MediaSubtype);
        Assert.Equal(["plain", "html"], body.OfType<TextPart>().Select(p => p.ContentType.MediaSubtype)); // the richest last, as clients expect
        Assert.Equal("Plain words", parsed.Text?.Trim());
        Assert.Contains("<b>words</b>", parsed.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Message_With_Only_Html_Gets_A_Text_Version_Made_From_It()
    {
        var composed = Composer.Compose(Message(text: null, html: "<p>Meeting at <b>noon</b></p><p>Bring the report.</p>"));

        var parsed = await ParseAsync(composed);

        Assert.Contains("Meeting at noon", parsed.Text, StringComparison.Ordinal);
        Assert.Contains("Bring the report.", parsed.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("<", parsed.Text, StringComparison.Ordinal);
        Assert.Contains("<b>noon</b>", parsed.Html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cyrillic_In_The_Subject_The_Names_And_The_Text_Comes_Back_Unchanged_And_The_Mail_Is_Seven_Bit()
    {
        var message = Message(
            subject: "Отчёт за ноябрь — итоги",
            text: "Добрый день!\nОтчёт во вложении.",
            html: "<p>Добрый день!</p>",
            to: [Person("anna@example.org", "Анна Смирнова")],
            cc: [Person("bob@example.org", "Борис «Босс» Иванов")]);

        var composed = Composer.Compose(message);

        var parsed = await ParseAsync(composed);
        Assert.Equal("Отчёт за ноябрь — итоги", parsed.Subject);
        Assert.Equal("Анна Смирнова", parsed.To[0].Name);
        Assert.Equal("Борис «Босс» Иванов", parsed.Cc[0].Name);
        Assert.Contains("Добрый день!", parsed.Text, StringComparison.Ordinal);
        Assert.Contains("Отчёт во вложении.", parsed.Text, StringComparison.Ordinal);
        Assert.All(composed.Raw.ToArray(), b => Assert.True(b < 128, "A byte outside seven bits would not pass every relay."));
    }

    [Fact]
    public async Task An_Attachment_With_A_Cyrillic_Name_Keeps_Its_Name_And_Its_Bytes()
    {
        var bytes = Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray();
        var composed = Composer.Compose(Message(attachments: [File("Договор №5 (черновик).pdf", bytes)]));

        var parsed = await ParseAsync(composed);

        var attachment = Assert.Single(parsed.Attachments);
        Assert.Equal("Договор №5 (черновик).pdf", attachment.FileName);
        Assert.Equal("application/pdf", attachment.ContentType);
        using var stream = new MemoryStream(composed.Raw.ToArray());
        var content = await new MimeMessageParser().ExtractAttachmentAsync(stream, attachment.PartId, Token);
        Assert.Equal(bytes, content?.Content);
        Assert.Contains("filename*", Wire(composed), StringComparison.Ordinal); // a name outside ASCII is encoded as RFC 2231 says
    }

    [Fact]
    public void Attachments_Go_Around_The_Body_In_A_Mixed_Part_In_Order()
    {
        var composed = Composer.Compose(Message(
            text: "see files",
            html: "<p>see files</p>",
            attachments: [File("one.pdf", [1]), File("two.zip", [2], "application/zip")]));

        var top = Assert.IsType<Multipart>(Load(composed).Body);

        Assert.Equal("mixed", top.ContentType.MediaSubtype);
        Assert.Equal("alternative", ((Multipart)top[0]).ContentType.MediaSubtype);
        Assert.Equal(["one.pdf", "two.zip"], top.Skip(1).Cast<MimePart>().Select(p => p.FileName));
    }

    [Fact]
    public void An_Attachment_Of_An_Unlisted_Or_Odd_Type_Is_Sent_As_Plain_Bytes_And_A_Name_Is_Cleaned()
    {
        var composed = Composer.Compose(Message(attachments:
        [
            File("../../page.html", [1], "text/html"),
            File("", [2], "not a type"),
            File("C:\\Users\\me\\report.pdf", [3]),
        ]));

        var parts = Assert.IsType<Multipart>(Load(composed).Body).Skip(1).Cast<MimePart>().ToList();

        Assert.Equal(["page.html", "attachment-2", "report.pdf"], parts.Select(p => p.FileName));
        Assert.Equal(["application/octet-stream", "application/octet-stream", "application/pdf"], parts.Select(p => p.ContentType.MimeType));
    }

    [Fact]
    public void Hidden_Recipients_Are_In_The_Envelope_And_In_No_Header()
    {
        var composed = Composer.Compose(Message(
            to: [Person("anna@example.org")],
            cc: [Person("cc@example.org")],
            bcc: [Person("secret@example.org")]));

        var message = Load(composed);

        Assert.Equal(["anna@example.org", "cc@example.org", "secret@example.org"], composed.Envelope.Recipients);
        Assert.Equal("me@vinette.test", composed.Envelope.From);
        Assert.Empty(message.Bcc);
        Assert.DoesNotContain("secret@example.org", Wire(composed), StringComparison.Ordinal);
        Assert.DoesNotContain("Bcc:", Wire(composed), StringComparison.Ordinal);
    }

    [Fact]
    public void A_Recipient_Named_Twice_Is_Delivered_To_Once_Whatever_The_Capitals()
    {
        var composed = Composer.Compose(Message(
            to: [Person("Anna@Example.org"), Person("anna@example.org")],
            cc: [Person("ANNA@example.ORG")]));

        Assert.Single(composed.Envelope.Recipients);
    }

    [Fact]
    public void A_New_Message_Gets_A_Message_Id_In_The_Domain_Of_The_Mailbox_And_Each_Is_Different()
    {
        var first = Composer.Compose(Message());
        var second = Composer.Compose(Message());

        Assert.EndsWith("@vinette.test", first.MessageId, StringComparison.Ordinal);
        Assert.NotEqual(first.MessageId, second.MessageId);
        Assert.Equal(first.MessageId, Load(first).MessageId);
        Assert.Matches(@"(?i)Message-ID:\s*<" + System.Text.RegularExpressions.Regex.Escape(first.MessageId) + ">", Wire(first));
    }

    [Theory]
    [InlineData("<kept-1@draft.example>", "kept-1@draft.example")]
    [InlineData("kept-2@draft.example", "kept-2@draft.example")]
    [InlineData("  <kept-3@draft.example>  ", "kept-3@draft.example")]
    public void A_Message_Id_That_Is_Given_Is_Kept(string given, string expected)
    {
        Assert.Equal(expected, Composer.Compose(Message(messageId: given)).MessageId);
    }

    [Theory]
    [InlineData("no-at-sign")]
    [InlineData("a b@c.d")]
    [InlineData("a@b@c.d")]
    public void A_Message_Id_That_Is_Not_One_Is_Refused(string given)
    {
        Assert.Throws<ArgumentException>(() => Composer.Compose(Message(messageId: given)));
    }

    [Fact]
    public void A_Reply_Names_The_Message_It_Answers_And_Ends_The_Thread_With_It()
    {
        var composed = Composer.Compose(Message(
            inReplyTo: "<last@example.org>",
            references: ["<first@example.org>", "second@example.org"]));

        var message = Load(composed);

        Assert.Equal("last@example.org", message.InReplyTo);
        Assert.Equal(["first@example.org", "second@example.org", "last@example.org"], message.References);
    }

    [Fact]
    public void A_Reply_Already_In_The_Thread_Is_Not_Added_Twice()
    {
        var message = Load(Composer.Compose(Message(inReplyTo: "b@example.org", references: ["a@example.org", "b@example.org"])));

        Assert.Equal(["a@example.org", "b@example.org"], message.References);
    }

    [Fact]
    public void A_Long_Thread_Keeps_Its_First_Message_And_The_Latest_Ones()
    {
        var thread = Enumerable.Range(1, 60).Select(i => $"m{i}@example.org").ToList();

        var message = Load(Composer.Compose(Message(inReplyTo: "m60@example.org", references: thread)));

        var references = message.References.ToList();
        Assert.Equal(OutgoingLimits.MaxReferences, references.Count);
        Assert.Equal("m1@example.org", references[0]);
        Assert.Equal("m60@example.org", references[^1]);
        Assert.Equal("m42@example.org", references[1]); // the 19 latest follow the first
    }

    [Fact]
    public void Ids_Of_Other_Peoples_Mail_That_Are_Malformed_Are_Left_Out_And_The_Send_Goes_On()
    {
        var message = Load(Composer.Compose(Message(
            inReplyTo: "garbage without at",
            references: ["good@example.org", "bad id@example.org", "<>", "also@good.example"])));

        Assert.Null(message.InReplyTo);
        Assert.Equal(["good@example.org", "also@good.example"], message.References);
    }

    [Fact]
    public void A_New_Message_Does_Not_List_Itself_In_Its_Own_References()
    {
        var message = Load(Composer.Compose(Message(messageId: "me@draft.example", references: ["me@draft.example", "a@example.org"])));

        Assert.Equal(["a@example.org"], message.References);
    }

    [Fact]
    public void Nothing_Typed_Into_A_Subject_Or_A_Name_Can_Add_A_Header()
    {
        var composed = Composer.Compose(Message(
            subject: "Hi\r\nBcc: evil@example.org\r\nX-Evil: 1",
            to: [Person("anna@example.org", "Anna\r\nX-Evil-Name: 2")]));

        var message = Load(composed);

        Assert.Empty(message.Bcc);
        Assert.False(message.Headers.Contains("X-Evil"));
        Assert.False(message.Headers.Contains("X-Evil-Name"));
        Assert.DoesNotContain("evil@example.org", Wire(composed).Split("\r\n\r\n")[0].Replace("Hi  Bcc: evil@example.org", string.Empty, StringComparison.Ordinal).Split("Subject:")[0], StringComparison.Ordinal);
        Assert.Equal("Hi  Bcc: evil@example.org  X-Evil: 1", message.Subject); // one line, as plain text of the subject
        Assert.Single(composed.Envelope.Recipients);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-at-sign")]
    [InlineData("a b@example.org")]
    [InlineData("<a@example.org>")]
    [InlineData("Anna <a@example.org>")]
    [InlineData("a@example.org, b@example.org")]
    [InlineData("a@example.org;b@example.org")]
    [InlineData("a@example.org\r\nBcc: x@example.org")]
    [InlineData("\"a\"@example.org")]
    public void Something_That_Is_Not_A_Plain_Address_Is_Refused(string address)
    {
        Assert.Throws<ArgumentException>(() => Composer.Compose(Message(to: [Person(address)])));
    }

    [Fact]
    public void A_Sender_That_Is_Not_An_Address_Is_Refused()
    {
        var message = Message() with { From = Person("not an address") };

        Assert.Throws<ArgumentException>(() => Composer.Compose(message));
    }

    [Fact]
    public void A_Message_Needs_A_Recipient_But_Not_Necessarily_A_To()
    {
        Assert.Throws<ArgumentException>(() => Composer.Compose(Message(to: [])));

        var onlyHidden = Composer.Compose(Message(to: [], bcc: [Person("hidden@example.org")]));

        Assert.Equal(["hidden@example.org"], onlyHidden.Envelope.Recipients);
    }

    [Fact]
    public void A_Hundred_Recipients_Are_Fine_And_A_Hundred_And_One_Are_Not()
    {
        MailAddressInfo[] many(int n) => [.. Enumerable.Range(1, n).Select(i => Person($"user{i}@example.org"))];

        Assert.Equal(100, Composer.Compose(Message(to: many(100))).Envelope.Recipients.Count);
        Assert.Throws<ArgumentException>(() => Composer.Compose(Message(to: many(101))));
    }

    [Fact]
    public void A_Subject_Can_Be_As_Long_As_The_Limit_And_No_Longer()
    {
        _ = Composer.Compose(Message(subject: new string('x', OutgoingLimits.MaxSubjectLength)));

        Assert.Throws<ArgumentException>(() => Composer.Compose(Message(subject: new string('x', OutgoingLimits.MaxSubjectLength + 1))));
    }

    [Fact]
    public void The_Date_Is_The_One_Given_Or_The_Time_Of_Composing()
    {
        var written = new DateTimeOffset(2026, 11, 16, 9, 30, 0, TimeSpan.FromHours(3));
        var clock = new FixedTime(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));

        var given = Load(Composer.Compose(Message(date: written)));
        var made = Load(new MimeMessageComposer(clock).Compose(Message()));

        Assert.Equal(written, given.Date);
        Assert.Equal(clock.GetUtcNow(), made.Date);
    }

    [Fact]
    public void Long_Lines_Are_Wrapped_And_Every_Line_End_Is_Crlf()
    {
        var composed = Composer.Compose(Message(
            text: new string('w', 5000) + "\nsecond line " + new string('я', 3000),
            html: "<p>" + new string('h', 4000) + "</p>"));

        var raw = composed.Raw.ToArray();

        var text = Encoding.ASCII.GetString(raw);
        Assert.All(text.Split("\r\n"), line => Assert.True(line.Length <= 998, "A line longer than 998 would be refused by relays."));
        Assert.DoesNotContain(raw.Select((b, i) => (b, i)), x => x.b == '\n' && (x.i == 0 || raw[x.i - 1] != '\r')); // no bare line feed
    }

    [Fact]
    public async Task The_Same_Message_Built_Twice_Differs_Only_In_Its_Id()
    {
        var first = Composer.Compose(Message(messageId: "same@draft.example", date: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        var second = Composer.Compose(Message(messageId: "same@draft.example", date: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        Assert.Equal(first.Raw.ToArray(), second.Raw.ToArray()); // nothing random besides the id: a draft saved again does not change by itself
        Assert.Equal((await ParseAsync(first)).MessageId, (await ParseAsync(second)).MessageId);
    }

    [Fact]
    public void An_Empty_Message_Can_Be_Built()
    {
        var composed = Composer.Compose(Message(subject: "", text: null, html: null));

        Assert.Equal(string.Empty, Load(composed).Subject ?? string.Empty);
        Assert.Single(composed.Envelope.Recipients);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
