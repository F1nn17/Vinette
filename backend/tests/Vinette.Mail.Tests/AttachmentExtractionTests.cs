using System.Text;
using Vinette.Core.Mail;

namespace Vinette.Mail.Tests;

/// <summary>Taking one attached file out of a mail, on the fixtures and on odd structure.</summary>
public class AttachmentExtractionTests
{
    private static readonly MimeMessageParser Parser = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static byte[] Fixture(string file) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", file));

    private static async Task<(ParsedMessage Parsed, byte[] Mail)> ParsedAsync(string file)
    {
        var mail = Fixture(file);
        return (await Parser.ParseAsync(new MemoryStream(mail), Token), mail);
    }

    private static Task<AttachmentContent?> ExtractAsync(byte[] mail, string partId) =>
        Parser.ExtractAttachmentAsync(new MemoryStream(mail), partId, Token);

    [Fact]
    public async Task Every_Listed_Attachment_Of_Every_Fixture_Can_Be_Taken_With_The_Listed_Size_And_Name()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fixtures"), "*.eml"))
        {
            var (parsed, mail) = await ParsedAsync(Path.GetFileName(file));

            foreach (var listed in parsed.Attachments)
            {
                var taken = await ExtractAsync(mail, listed.PartId);

                Assert.NotNull(taken);
                Assert.Equal(listed, taken.Attachment);
                Assert.Equal(listed.Size, taken.Content.Length);
            }
        }
    }

    [Fact]
    public async Task The_Bytes_Are_The_Decoded_Content()
    {
        var (parsed, mail) = await ParsedAsync("06-nested-multipart.eml");
        var notes = parsed.Attachments.Single(a => a.FileName == "notes.txt");

        var taken = await ExtractAsync(mail, notes.PartId);

        Assert.NotNull(taken);
        Assert.False(string.IsNullOrWhiteSpace(Encoding.UTF8.GetString(taken.Content)));
        Assert.DoesNotContain("Content-Transfer-Encoding", Encoding.UTF8.GetString(taken.Content), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_Attached_Mail_Is_Taken_As_A_Mail_With_Its_Own_Headers()
    {
        var (parsed, mail) = await ParsedAsync("09-attached-rfc822.eml");

        var taken = await ExtractAsync(mail, Assert.Single(parsed.Attachments).PartId);

        Assert.NotNull(taken);
        Assert.Contains("Subject: Inner message", Encoding.UTF8.GetString(taken.Content), StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_Is_Not_Listed_As_An_Attachment_Cannot_Be_Taken()
    {
        var (parsed, mail) = await ParsedAsync("06-nested-multipart.eml");

        Assert.Null(await ExtractAsync(mail, parsed.InlineParts.Single().PartId)); // an inline picture
        Assert.Null(await ExtractAsync(mail, "1")); // the whole multipart
        Assert.Null(await ExtractAsync(mail, "1.1")); // a container
        Assert.Null(await ExtractAsync(mail, "1.1.1.1")); // a body part
        Assert.Null(await ExtractAsync(mail, "9.9.9"));
        Assert.Null(await ExtractAsync(mail, "x"));
    }

    [Fact]
    public async Task A_Mail_Without_Attachments_And_Garbage_Give_Nothing_Not_An_Error()
    {
        Assert.Null(await ExtractAsync(Fixture("01-plain-ascii.eml"), "1"));
        Assert.Null(await ExtractAsync([], "1"));
        Assert.Null(await ExtractAsync(Encoding.ASCII.GetBytes("\u0000\u0001garbage"), "1.1"));
    }

    [Fact]
    public async Task The_Same_Part_Id_Names_The_Same_Part_As_In_The_List()
    {
        var (parsed, mail) = await ParsedAsync("08-attachments-unicode-names.eml");

        foreach (var listed in parsed.Attachments)
        {
            var taken = await ExtractAsync(mail, listed.PartId);
            Assert.Equal(listed.FileName, taken?.Attachment.FileName);
        }

        Assert.Equal(2, parsed.Attachments.Select(a => a.PartId).Distinct().Count());
    }

    [Fact]
    public async Task A_Name_With_A_Path_Is_Already_A_Plain_Name()
    {
        var (parsed, mail) = await ParsedAsync("11-dangerous-attachments.eml");

        foreach (var listed in parsed.Attachments)
        {
            var taken = await ExtractAsync(mail, listed.PartId);
            Assert.DoesNotContain('/', taken!.Attachment.FileName);
            Assert.DoesNotContain('\\', taken.Attachment.FileName);
        }
    }
}
