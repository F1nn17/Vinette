using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Vinette.Api.Auth;
using Vinette.TestSupport;

namespace Vinette.Api.Tests;

/// <summary>Opening a message through the real application against a real Dovecot, on the fixture mails.</summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed partial class MessageApiTests(DovecotFixture dovecot) : IDisposable
{
    private readonly WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseSetting("Imap:Host", dovecot.Host);
        b.UseSetting("Imap:Port", dovecot.ImapPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        b.UseSetting("Imap:Security", "None");
    });

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => factory.Dispose();

    [GeneratedRegex(@"<\s*(script|iframe|object|embed|form|input|svg|math|meta|base|link)\b|\bon[a-z]+\s*=|javascript\s*:|@import|url\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Active();

    private async Task<(HttpClient Client, string Cookie)> SignInAsync(MailAccount account)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = await CsrfClient.PostJsonAsync(client, "/api/v1/auth/login", new { address = account.Address, password = account.Password, rememberMe = false }, Token);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(SessionCookie.Name + "=", StringComparison.Ordinal)).Split(';')[0];
        return (client, cookie);
    }

    private static async Task<(HttpResponseMessage Response, JsonElement Body)> GetAsync(HttpClient client, string cookie, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", cookie);
        var response = await client.SendAsync(request, Token);
        var text = await response.Content.ReadAsStringAsync(Token);
        return (response, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement);
    }

    /// <summary>The ids of Bob's seeded mails by subject.</summary>
    private static async Task<Dictionary<string, string>> IdsBySubjectAsync(HttpClient client, string cookie)
    {
        var (_, page) = await GetAsync(client, cookie, "/api/v1/folders/INBOX/messages?limit=100&preview=false");
        return page.GetProperty("items").EnumerateArray()
            .GroupBy(i => i.GetProperty("subject").GetString()!)
            .ToDictionary(g => g.Key, g => g.First().GetProperty("id").GetString()!);
    }

    private async Task<JsonElement> OpenAsync(string subject)
    {
        var (client, cookie) = await SignInAsync(DovecotFixture.Bob);
        var ids = await IdsBySubjectAsync(client, cookie);
        var (response, body) = await GetAsync(client, cookie, $"/api/v1/messages/{ids[subject]}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return body;
    }

    [Fact]
    public async Task A_Plain_Text_Mail_Comes_As_Text_Made_Into_Html()
    {
        var message = await OpenAsync("Plain ASCII message");

        Assert.Equal("text", message.GetProperty("bodyFormat").GetString());
        Assert.Contains("this is a plain message.", message.GetProperty("body").GetString(), StringComparison.Ordinal);
        Assert.Equal("anna@example.org", message.GetProperty("from").GetProperty("address").GetString());
        Assert.Empty(message.GetProperty("attachments").EnumerateArray());
    }

    [Fact]
    public async Task An_Alternative_Mail_Prefers_The_Html_And_Reads_Cyrillic()
    {
        var message = await OpenAsync("Привет, мир");

        Assert.Equal("html", message.GetProperty("bodyFormat").GetString());
        Assert.Contains("<b>проверка</b>", message.GetProperty("body").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Тест KOI8-R", "Это письмо в кодировке KOI8-R.")]
    [InlineData("Отчёт за квартал", "Письмо в кодировке Windows-1251, цифры: №1.")]
    [InlineData("Base64 тема ✓", "Тело письма в base64.")]
    public async Task Mails_In_Legacy_Charsets_Are_Readable(string subject, string text)
    {
        var message = await OpenAsync(subject);

        Assert.Contains(text, message.GetProperty("body").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_Attachments_Are_Listed_And_The_Inline_Picture_Is_Not_One_Of_Them()
    {
        var message = await OpenAsync("Nested multipart");

        var files = message.GetProperty("attachments").EnumerateArray().ToList();
        Assert.Equal(["notes.txt", "report.pdf"], files.Select(f => f.GetProperty("fileName").GetString()!).Order());
        Assert.All(files, f => Assert.True(f.GetProperty("size").GetInt64() > 0));
        Assert.All(files, f => Assert.Matches(@"^\d+(\.\d+)*$", f.GetProperty("partId").GetString()!));
        var body = message.GetProperty("body").GetString()!;
        Assert.DoesNotContain("cid:", body, StringComparison.Ordinal); // the inline picture is in the body itself
        Assert.Contains("src=\"data:image/png;base64,", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Mail_With_Only_An_Inline_Picture_Shows_It()
    {
        var message = await OpenAsync("Inline image only");

        var body = message.GetProperty("body").GetString()!;
        Assert.Contains("src=\"data:image/png;base64,", body, StringComparison.Ordinal);
        Assert.Contains("Only an inline image.", body, StringComparison.Ordinal);
        Assert.Equal(0, message.GetProperty("blockedExternalResources").GetInt32());
        Assert.Empty(message.GetProperty("attachments").EnumerateArray());
    }

    [Fact]
    public async Task An_Attached_Mail_Is_An_Attachment_With_Its_Own_Name()
    {
        var message = await OpenAsync("Fwd attached message");

        var attachment = Assert.Single(message.GetProperty("attachments").EnumerateArray());
        Assert.Equal("inner.eml", attachment.GetProperty("fileName").GetString());
        Assert.True(attachment.GetProperty("isMessage").GetBoolean());
    }

    [Fact]
    public async Task Dangerous_File_Names_Come_Out_As_Plain_Names()
    {
        var message = await OpenAsync("Dangerous attachments");

        var names = message.GetProperty("attachments").EnumerateArray().Select(f => f.GetProperty("fileName").GetString()!).ToList();
        Assert.Contains("setup.exe", names);
        Assert.All(names, n => Assert.DoesNotContain("/", n, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_Malicious_Html_Mail_Comes_Out_Without_Anything_Active()
    {
        var message = await OpenAsync("Urgent: verify your account");

        var body = message.GetProperty("body").GetString()!;
        Assert.Equal("html", message.GetProperty("bodyFormat").GetString());
        Assert.DoesNotMatch(Active(), body);
        Assert.Contains("safe-marker", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task External_Pictures_Are_Taken_Out_And_Counted()
    {
        var message = await OpenAsync("Weekly newsletter");

        var body = message.GetProperty("body").GetString()!;
        Assert.True(message.GetProperty("blockedExternalResources").GetInt32() >= 3);
        Assert.DoesNotContain("http://tracker", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(Active(), body);
    }

    [Fact]
    public async Task A_Mail_Without_A_Body_Is_Empty_Not_An_Error()
    {
        var message = await OpenAsync("Attachment only");

        Assert.Equal("empty", message.GetProperty("bodyFormat").GetString());
        Assert.Equal(string.Empty, message.GetProperty("body").GetString());
        Assert.Single(message.GetProperty("attachments").EnumerateArray());
    }

    [Fact]
    public async Task A_Mail_With_Broken_Headers_Opens()
    {
        // Found by its sender: the server and the parser read its broken subject differently.
        var (client, cookie) = await SignInAsync(DovecotFixture.Bob);
        var (_, page) = await GetAsync(client, cookie, "/api/v1/folders/INBOX/messages?limit=100&preview=false");
        var id = page.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("from").GetProperty("address").GetString() == "broken@example.org").GetProperty("id").GetString();
        var (response, message) = await GetAsync(client, cookie, $"/api/v1/messages/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Contains("bare LF", message.GetProperty("body").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, message.GetProperty("date").ValueKind);
    }

    [Fact]
    public async Task Every_Recipient_Is_Listed()
    {
        var message = await OpenAsync("Many recipients");

        Assert.True(message.GetProperty("to").GetArrayLength() >= 30);
        Assert.True(message.GetProperty("cc").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task The_Answer_Is_Not_Kept_By_Anyone_On_The_Way()
    {
        var (client, cookie) = await SignInAsync(DovecotFixture.Bob);
        var ids = await IdsBySubjectAsync(client, cookie);

        var (response, _) = await GetAsync(client, cookie, $"/api/v1/messages/{ids["Plain ASCII message"]}");

        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.Contains("private", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Opening_A_Message_Does_Not_Mark_It_As_Read()
    {
        var account = await dovecot.CreateAccountAsync($"api{Guid.NewGuid():N}"[..14], Token);
        await dovecot.SaveFixtureAsync(account, "INBOX", "01-plain-ascii.eml", Token);
        var (client, cookie) = await SignInAsync(account);
        var ids = await IdsBySubjectAsync(client, cookie);

        var (_, opened) = await GetAsync(client, cookie, $"/api/v1/messages/{ids["Plain ASCII message"]}");
        var (_, list) = await GetAsync(client, cookie, "/api/v1/folders/INBOX/messages");

        Assert.False(opened.GetProperty("seen").GetBoolean());
        Assert.False(list.GetProperty("items")[0].GetProperty("seen").GetBoolean());
    }

    [Fact]
    public async Task A_Message_That_Does_Not_Exist_Is_404_Without_The_Folder_Name()
    {
        var (client, cookie) = await SignInAsync(DovecotFixture.Bob);
        var ids = await IdsBySubjectAsync(client, cookie);
        var parts = ids["Plain ASCII message"].Split('.');

        var (missing, body) = await GetAsync(client, cookie, $"/api/v1/messages/{parts[0]}.{parts[1]}.99999");
        var (folder, folderBody) = await GetAsync(client, cookie, "/api/v1/messages/U2VjcmV0.1.1");

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("not_found", body.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, folder.StatusCode);
        Assert.DoesNotContain("Secret", folderBody.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_Id_Of_Another_Folder_Version_Is_409()
    {
        var (client, cookie) = await SignInAsync(DovecotFixture.Bob);
        var ids = await IdsBySubjectAsync(client, cookie);
        var parts = ids["Plain ASCII message"].Split('.');
        var stale = $"{parts[0]}.{uint.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) + 1}.{parts[2]}";

        var (response, body) = await GetAsync(client, cookie, $"/api/v1/messages/{stale}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("uid_validity_changed", body.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("SU5CT1g.0.1")]
    [InlineData("SU5CT1g.1")]
    [InlineData("SU5CT1g.1.1.1")]
    public async Task A_Malformed_Id_Is_A_400(string id)
    {
        var (client, cookie) = await SignInAsync(DovecotFixture.Bob);

        var (response, body) = await GetAsync(client, cookie, $"/api/v1/messages/{id}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task A_Message_Needs_A_Session()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/messages/SU5CT1g.1.1", Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Everything_That_Leaves_Is_Inert_For_Every_Fixture()
    {
        var (client, cookie) = await SignInAsync(DovecotFixture.Bob);
        var ids = await IdsBySubjectAsync(client, cookie);

        foreach (var id in ids.Values)
        {
            var (response, body) = await GetAsync(client, cookie, $"/api/v1/messages/{id}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotMatch(Active(), body.GetProperty("body").GetString()!);
        }
    }
}
