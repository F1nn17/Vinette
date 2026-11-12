using System.Text;
using System.Text.Json;
using Vinette.Core.Mail;

namespace Vinette.Mail.Tests;

/// <summary>The MIME parser against the fixture mails (expectations in tests/fixtures/manifest.json) and against hostile structure.</summary>
public class MimeMessageParserTests
{
    private sealed record Attachment(string Name, string ContentType);

    private sealed record Fixture(
        string File,
        string Subject,
        string From,
        string[] TextContains,
        string[] HtmlContains,
        Attachment[] Attachments,
        string[] InlineContentIds,
        string? AuthenticationResults);

    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "fixtures");

    private static readonly MimeMessageParser Parser = new();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Fixture[] LoadManifest() =>
        JsonSerializer.Deserialize<Fixture[]>(File.ReadAllText(Path.Combine(Dir, "manifest.json")), JsonOptions)!;

    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string>();
        foreach (var fixture in LoadManifest())
        {
            data.Add(fixture.File);
        }

        return data;
    }

    private static async Task<ParsedMessage> ParseFixtureAsync(string file)
    {
        await using var stream = File.OpenRead(Path.Combine(Dir, file));
        return await Parser.ParseAsync(stream, Token);
    }

    private static Task<ParsedMessage> ParseTextAsync(string eml)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(eml.Replace("\n", "\r\n", StringComparison.Ordinal)));
        return Parser.ParseAsync(stream, Token);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task Every_Fixture_Is_Taken_Apart_As_Described(string file)
    {
        var fixture = LoadManifest().Single(f => f.File == file);

        var parsed = await ParseFixtureAsync(file);

        Assert.Equal(fixture.Subject, parsed.Subject);
        Assert.Equal(fixture.From, parsed.From?.Address);
        Assert.All(fixture.TextContains, f => Assert.Contains(f, parsed.Text ?? string.Empty, StringComparison.Ordinal));
        Assert.All(fixture.HtmlContains, f => Assert.Contains(f, parsed.Html ?? string.Empty, StringComparison.Ordinal));

        // File names come out as plain names: "../../setup.exe" is "setup.exe".
        var expectedAttachments = fixture.Attachments
            .Select(a => (Name: AttachmentFileName.Sanitize(a.Name, "x"), a.ContentType))
            .OrderBy(a => a.Name, StringComparer.Ordinal);
        var attachments = parsed.Attachments
            .Select(a => (a.FileName, a.ContentType))
            .OrderBy(a => a.FileName, StringComparer.Ordinal);
        Assert.Equal(expectedAttachments, attachments);

        Assert.Equal(fixture.InlineContentIds.Order(), parsed.InlineParts.Select(p => p.ContentId).Order());

        if (fixture.AuthenticationResults is null)
        {
            Assert.Empty(parsed.AuthenticationResults);
        }
        else
        {
            Assert.Contains($"dmarc={fixture.AuthenticationResults}", Assert.Single(parsed.AuthenticationResults), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_Mixed_Related_Alternative_Mail_Gives_Both_Bodies_The_Inline_Image_And_The_Two_Files_With_Their_Places()
    {
        var parsed = await ParseFixtureAsync("06-nested-multipart.eml");

        Assert.NotNull(parsed.Text);
        Assert.NotNull(parsed.Html);
        Assert.All(parsed.Attachments, a => Assert.StartsWith("1.", a.PartId, StringComparison.Ordinal));
        Assert.Equal(2, parsed.Attachments.Select(a => a.PartId).Distinct().Count());
        Assert.Equal(["application/pdf", "text/plain"], parsed.Attachments.Select(a => a.ContentType).Order());
        var logo = Assert.Single(parsed.InlineParts);
        Assert.Equal("logo@example.org", logo.ContentId);
        Assert.Equal("image/png", logo.ContentType);
        Assert.True(logo.Size > 0);
        Assert.Equal(logo.Size, logo.Content.Length);
        Assert.Equal("image/png", InlineImage.Sniff(logo.Content));
        Assert.All(parsed.Attachments, a => Assert.True(a.Size > 0));
    }

    [Fact]
    public async Task An_Inline_Picture_Over_The_Embedding_Limit_Is_Listed_But_Its_Bytes_Are_Not_Kept()
    {
        var big = Convert.ToBase64String(new byte[MessageLimits.MaxInlineImageBytes + 10], Base64FormattingOptions.InsertLineBreaks);

        var parsed = await ParseTextAsync($"""
            From: a@example.org
            Subject: x
            MIME-Version: 1.0
            Content-Type: multipart/related; boundary="b"

            --b
            Content-Type: text/html

            <img src="cid:big@x">
            --b
            Content-Type: image/png
            Content-ID: <big@x>
            Content-Transfer-Encoding: base64

            {big}
            --b--
            """);

        var part = Assert.Single(parsed.InlineParts);
        Assert.Equal(MessageLimits.MaxInlineImageBytes + 10, part.Size);
        Assert.Empty(part.Content);
    }

    [Fact]
    public async Task An_Attached_Mail_Is_One_Attachment_And_Its_Insides_Are_Not_Mixed_Into_The_Message()
    {
        var parsed = await ParseFixtureAsync("09-attached-rfc822.eml");

        var attached = Assert.Single(parsed.Attachments);
        Assert.True(attached.IsMessage);
        Assert.Equal("message/rfc822", attached.ContentType);
        Assert.Equal("inner.eml", attached.FileName);
        Assert.Equal("See the attached message.", parsed.Text?.Trim());
        Assert.True(attached.Size > 0);
    }

    [Fact]
    public async Task A_Mail_With_Only_An_Html_Part_Has_No_Text_And_One_With_Only_A_File_Has_No_Body()
    {
        var htmlOnly = await ParseFixtureAsync("13-html-only.eml");
        var fileOnly = await ParseFixtureAsync("14-attachment-only.eml");

        Assert.Null(htmlOnly.Text);
        Assert.NotNull(htmlOnly.Html);
        Assert.Null(fileOnly.Text);
        Assert.Null(fileOnly.Html);
        Assert.Equal("only.pdf", Assert.Single(fileOnly.Attachments).FileName);
    }

    [Fact]
    public async Task A_Mail_With_Broken_Headers_Is_Read_As_Far_As_It_Can_Be()
    {
        var parsed = await ParseFixtureAsync("15-broken-headers.eml");

        Assert.Null(parsed.Date);
        Assert.Null(parsed.MessageId);
        Assert.Contains("Body with bare LF line endings", parsed.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task All_Recipients_Come_Out_Not_Only_The_First_Few()
    {
        var parsed = await ParseFixtureAsync("20-many-recipients.eml");

        Assert.True(parsed.To.Count >= 30);
        Assert.NotEmpty(parsed.Cc);
    }

    [Fact]
    public async Task Dangerous_File_Names_Come_Out_As_Plain_Names()
    {
        var parsed = await ParseFixtureAsync("11-dangerous-attachments.eml");

        Assert.Contains("setup.exe", parsed.Attachments.Select(a => a.FileName));
        Assert.All(parsed.Attachments, a => Assert.DoesNotContain("/", a.FileName, StringComparison.Ordinal));
        Assert.All(parsed.Attachments, a => Assert.DoesNotContain("\\", a.FileName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_Part_Without_A_Name_Gets_One_From_Its_Place()
    {
        var parsed = await ParseTextAsync("""
            From: a@example.org
            Subject: x
            MIME-Version: 1.0
            Content-Type: multipart/mixed; boundary="b"

            --b
            Content-Type: text/plain

            hello
            --b
            Content-Type: application/octet-stream
            Content-Transfer-Encoding: base64

            AAECAwQ=
            --b--
            """);

        var file = Assert.Single(parsed.Attachments);
        Assert.Equal("attachment-1.2", file.FileName);
        Assert.Equal(5, file.Size); // decoded, not the length of the base64 text
    }

    [Fact]
    public async Task A_Part_Named_Only_In_The_Content_Type_Is_An_Attachment()
    {
        var parsed = await ParseTextAsync("""
            From: a@example.org
            Subject: x
            MIME-Version: 1.0
            Content-Type: multipart/mixed; boundary="b"

            --b
            Content-Type: text/plain

            hello
            --b
            Content-Type: application/pdf; name="report.pdf"
            Content-Transfer-Encoding: base64

            AAEC
            --b--
            """);

        Assert.Equal("report.pdf", Assert.Single(parsed.Attachments).FileName);
    }

    [Fact]
    public async Task A_Mail_Nested_Far_Deeper_Than_Any_Real_One_Is_Not_Followed_To_The_End_And_Does_Not_Fail()
    {
        var builder = new StringBuilder("From: a@example.org\nSubject: deep\nMIME-Version: 1.0\n");
        const int depth = 200;
        for (var i = 0; i < depth; i++)
        {
            builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"Content-Type: multipart/mixed; boundary=\"b{i}\"\n\n--b{i}\n");
        }

        builder.Append("Content-Type: application/octet-stream; name=\"deep.bin\"\n\nxx\n");
        for (var i = depth - 1; i >= 0; i--)
        {
            builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"--b{i}--\n");
        }

        var parsed = await ParseTextAsync(builder.ToString());

        Assert.Equal("deep", parsed.Subject);
        Assert.DoesNotContain(parsed.Attachments, a => a.FileName == "deep.bin"); // deeper than the limit: ignored
    }

    [Fact]
    public async Task Nothing_At_All_Gives_An_Empty_Message_Not_An_Error()
    {
        var parsed = await Parser.ParseAsync(new MemoryStream(), Token);

        Assert.Equal(string.Empty, parsed.Subject);
        Assert.Null(parsed.From);
        Assert.Null(parsed.Text);
        Assert.Null(parsed.Html);
        Assert.Empty(parsed.Attachments);
    }

    [Fact]
    public async Task Random_Bytes_Never_Make_The_Parser_Throw()
    {
        var random = new Random(42);
        for (var round = 0; round < 50; round++)
        {
            var bytes = new byte[random.Next(0, 4000)];
            random.NextBytes(bytes);

            var parsed = await Parser.ParseAsync(new MemoryStream(bytes), Token);

            Assert.NotNull(parsed);
        }
    }

    [Fact]
    public async Task Text_Of_A_Mail_Stays_Text_Nothing_Is_Interpreted()
    {
        var parsed = await ParseTextAsync("""
            From: a@example.org
            Subject: <script>alert(1)</script>
            Content-Type: text/html

            <script>alert(2)</script>
            """);

        Assert.Equal("<script>alert(1)</script>", parsed.Subject);
        Assert.Contains("<script>alert(2)</script>", parsed.Html, StringComparison.Ordinal); // raw: the sanitizer comes next
    }
}
