using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Vinette.Api.Auth;
using Vinette.TestSupport;

namespace Vinette.Api.Tests;

/// <summary>Downloading attachments through the real application against a real Dovecot: the headers are the point.</summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class AttachmentDownloadApiTests(DovecotFixture dovecot) : IDisposable
{
    private readonly WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseSetting("Imap:Host", dovecot.Host);
        b.UseSetting("Imap:Port", dovecot.ImapPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        b.UseSetting("Imap:Security", "None");
    });

    private static readonly string[] Shown = ["text/html", "image/svg+xml", "application/xhtml+xml", "text/xml", "application/xml"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => factory.Dispose();

    private async Task<(HttpClient Client, string Cookie)> SignInAsync()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = await CsrfClient.PostJsonAsync(client, "/api/v1/auth/login", new { address = DovecotFixture.Bob.Address, password = DovecotFixture.Bob.Password, rememberMe = false }, Token);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(SessionCookie.Name + "=", StringComparison.Ordinal)).Split(';')[0];
        return (client, cookie);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string cookie, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", cookie);
        return await client.SendAsync(request, Token);
    }

    /// <summary>The message with this subject and its attachments as the message endpoint lists them.</summary>
    private async Task<(HttpClient Client, string Cookie, string Id, List<JsonElement> Attachments)> MessageAsync(string subject)
    {
        var (client, cookie) = await SignInAsync();
        var list = JsonDocument.Parse(await (await SendAsync(client, cookie, "/api/v1/folders/INBOX/messages?limit=100&preview=false")).Content.ReadAsStringAsync(Token)).RootElement;
        var id = list.GetProperty("items").EnumerateArray().First(i => i.GetProperty("subject").GetString() == subject).GetProperty("id").GetString()!;
        var message = JsonDocument.Parse(await (await SendAsync(client, cookie, $"/api/v1/messages/{id}")).Content.ReadAsStringAsync(Token)).RootElement;
        return (client, cookie, id, [.. message.GetProperty("attachments").EnumerateArray().Select(a => a.Clone())]);
    }

    private static JsonElement Named(List<JsonElement> attachments, string name) =>
        attachments.Single(a => a.GetProperty("fileName").GetString() == name);

    [Fact]
    public async Task An_Attachment_Is_Sent_As_A_Download_With_The_Safety_Headers()
    {
        var (client, cookie, id, files) = await MessageAsync("Nested multipart");
        var pdf = Named(files, "report.pdf");

        var response = await SendAsync(client, cookie, $"/api/v1/messages/{id}/attachments/{pdf.GetProperty("partId").GetString()}");
        var bytes = await response.Content.ReadAsByteArrayAsync(Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("report.pdf", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Contains("sandbox", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.Equal(pdf.GetProperty("size").GetInt64(), bytes.Length);
    }

    [Theory]
    [InlineData("logo.svg")]
    [InlineData("page.html")]
    public async Task Svg_And_Html_Attachments_Are_Never_Sent_With_A_Type_The_Browser_Would_Show(string name)
    {
        var (client, cookie, id, files) = await MessageAsync("Dangerous attachments");

        var response = await SendAsync(client, cookie, $"/api/v1/messages/{id}/attachments/{Named(files, name).GetProperty("partId").GetString()}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType); // the mail said "inline"
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.StartsWith("<", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal); // the bytes are untouched
    }

    [Fact]
    public async Task A_Name_With_A_Path_Is_Sent_As_A_Plain_Name()
    {
        var (client, cookie, id, files) = await MessageAsync("Dangerous attachments");

        var response = await SendAsync(client, cookie, $"/api/v1/messages/{id}/attachments/{Named(files, "setup.exe").GetProperty("partId").GetString()}");

        var disposition = string.Join(" ", response.Content.Headers.GetValues("Content-Disposition"));
        Assert.Contains("setup.exe", disposition, StringComparison.Ordinal);
        Assert.DoesNotContain("..", disposition, StringComparison.Ordinal);
        Assert.DoesNotContain("/", disposition, StringComparison.Ordinal);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_Cyrillic_Name_Is_Sent_In_The_Encoded_Form_As_Well()
    {
        var (client, cookie, id, files) = await MessageAsync("Unicode attachment names");
        var any = files[0];

        var response = await SendAsync(client, cookie, $"/api/v1/messages/{id}/attachments/{any.GetProperty("partId").GetString()}");

        var disposition = string.Join(" ", response.Content.Headers.GetValues("Content-Disposition"));
        Assert.Contains("attachment", disposition, StringComparison.Ordinal);
        Assert.Contains("filename*=UTF-8''", disposition, StringComparison.Ordinal);
        Assert.Equal(any.GetProperty("fileName").GetString(), response.Content.Headers.ContentDisposition?.FileNameStar);
    }

    [Fact]
    public async Task An_Attached_Mail_Is_Downloaded_As_A_Mail_File()
    {
        var (client, cookie, id, files) = await MessageAsync("Fwd attached message");

        var response = await SendAsync(client, cookie, $"/api/v1/messages/{id}/attachments/{files[0].GetProperty("partId").GetString()}");

        Assert.Equal("message/rfc822", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("Subject: Inner message", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_Is_Not_An_Attachment_Is_404()
    {
        var (client, cookie, id, _) = await MessageAsync("Nested multipart");

        foreach (var partId in new[] { "1", "1.1", "1.1.1.1", "9.9", "42" })
        {
            var response = await SendAsync(client, cookie, $"/api/v1/messages/{id}/attachments/{partId}");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("not_found", JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token)).RootElement.GetProperty("code").GetString());
        }
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1..2")]
    [InlineData("1.")]
    [InlineData(".1")]
    [InlineData("1.2.x")]
    [InlineData("99999")]
    public async Task A_Malformed_Part_Id_Is_A_400(string partId)
    {
        var (client, cookie, id, _) = await MessageAsync("Nested multipart");

        var response = await SendAsync(client, cookie, $"/api/v1/messages/{id}/attachments/{partId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_Message_That_Is_Gone_Or_From_Another_Folder_Version_Gives_The_Usual_Errors()
    {
        var (client, cookie, id, files) = await MessageAsync("Nested multipart");
        var parts = id.Split('.');
        var partId = files[0].GetProperty("partId").GetString();

        var gone = await SendAsync(client, cookie, $"/api/v1/messages/{parts[0]}.{parts[1]}.99999/attachments/{partId}");
        var stale = await SendAsync(client, cookie, $"/api/v1/messages/{parts[0]}.{uint.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) + 1}.{parts[2]}/attachments/{partId}");

        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task A_Download_Needs_A_Session()
    {
        var response = await factory.CreateClient().GetAsync("/api/v1/messages/SU5CT1g.1.1/attachments/1.2", Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Every_Attachment_Of_Every_Fixture_Is_A_Safe_Download()
    {
        var (client, cookie) = await SignInAsync();
        var list = JsonDocument.Parse(await (await SendAsync(client, cookie, "/api/v1/folders/INBOX/messages?limit=100&preview=false")).Content.ReadAsStringAsync(Token)).RootElement;
        var downloads = 0;

        foreach (var item in list.GetProperty("items").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString();
            var message = JsonDocument.Parse(await (await SendAsync(client, cookie, $"/api/v1/messages/{id}")).Content.ReadAsStringAsync(Token)).RootElement;
            foreach (var attachment in message.GetProperty("attachments").EnumerateArray())
            {
                var response = await SendAsync(client, cookie, $"/api/v1/messages/{id}/attachments/{attachment.GetProperty("partId").GetString()}");

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
                Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
                Assert.DoesNotContain(response.Content.Headers.ContentType?.MediaType, Shown);
                downloads++;
            }
        }

        Assert.True(downloads >= 6, $"Only {downloads} attachments were downloaded.");
    }
}
