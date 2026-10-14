using System.Text.Json;
using MimeKit;

namespace Vinette.Mail.Tests;

/// <summary>Keeps tests/fixtures honest: every .eml is described in manifest.json and parses as described.</summary>
public class FixtureTests
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

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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

    private static string? FileNameOf(MimeEntity entity) =>
        entity.ContentDisposition?.FileName ?? entity.ContentType.Name;

    /// <summary>Attachment = disposition "attachment", or "inline" with a file name and no Content-ID.</summary>
    private static IEnumerable<MimeEntity> AttachmentsOf(MimeMessage message) =>
        message.BodyParts.Concat(message.BodyParts.OfType<MessagePart>()).Distinct().Where(p =>
            p.ContentDisposition?.Disposition == ContentDisposition.Attachment
            || (p.ContentDisposition?.Disposition == ContentDisposition.Inline && p.ContentId is null && FileNameOf(p) is not null));

    [Fact]
    public void Manifest_Lists_Every_Eml_File()
    {
        var onDisk = Directory.GetFiles(Dir, "*.eml").Select(Path.GetFileName).Order().ToArray();
        var listed = LoadManifest().Select(f => f.File).Order().ToArray();

        Assert.Equal(onDisk, listed);
    }

    [Fact]
    public void Readme_Mentions_Every_Fixture()
    {
        var readme = File.ReadAllText(Path.Combine(Dir, "README.md"));

        Assert.All(LoadManifest(), f => Assert.Contains($"`{f.File}`", readme, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task Fixture_Parses_As_Described(string file)
    {
        var fixture = LoadManifest().Single(f => f.File == file);

        var message = await MimeMessage.LoadAsync(Path.Combine(Dir, file), TestContext.Current.CancellationToken);

        Assert.Equal(fixture.Subject, message.Subject);
        Assert.Equal(fixture.From, message.From.Mailboxes.Single().Address);

        var text = message.TextBody ?? string.Empty;
        Assert.All(fixture.TextContains, fragment => Assert.Contains(fragment, text, StringComparison.Ordinal));

        var html = message.HtmlBody ?? string.Empty;
        Assert.All(fixture.HtmlContains, fragment => Assert.Contains(fragment, html, StringComparison.Ordinal));

        var attachments = AttachmentsOf(message)
            .Select(p => new Attachment(FileNameOf(p)!, p.ContentType.MimeType))
            .OrderBy(a => a.Name, StringComparer.Ordinal);
        Assert.Equal(fixture.Attachments.OrderBy(a => a.Name, StringComparer.Ordinal), attachments);

        var contentIds = message.BodyParts.Select(p => p.ContentId).Where(id => id is not null).Order();
        Assert.Equal(fixture.InlineContentIds.Order(), contentIds);

        var authResults = message.Headers[HeaderId.AuthenticationResults];
        if (fixture.AuthenticationResults is null)
        {
            Assert.Null(authResults);
        }
        else
        {
            Assert.Contains($"dmarc={fixture.AuthenticationResults}", authResults, StringComparison.Ordinal);
        }
    }
}
