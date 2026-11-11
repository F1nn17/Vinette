using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Vinette.Api.Auth;
using Vinette.TestSupport;

namespace Vinette.Api.Tests;

/// <summary>The message list endpoint through the real application against a real Dovecot.</summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class MessageListApiTests(DovecotFixture dovecot) : IDisposable
{
    private readonly WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseSetting("Imap:Host", dovecot.Host);
        b.UseSetting("Imap:Port", dovecot.ImapPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        b.UseSetting("Imap:Security", "None");
    });

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => factory.Dispose();

    private async Task<(HttpClient Client, string Cookie)> SignInAsync(MailAccount account)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = await CsrfClient.PostJsonAsync(
            client,
            "/api/v1/auth/login",
            new { address = account.Address, password = account.Password, rememberMe = false },
            Token);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(SessionCookie.Name + "=", StringComparison.Ordinal)).Split(';')[0];
        return (client, cookie);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(HttpClient client, string cookie, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", cookie);
        var response = await client.SendAsync(request, Token);
        var text = await response.Content.ReadAsStringAsync(Token);
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement);
    }

    [Fact]
    public async Task Pages_Are_Walked_With_The_Next_Cursor_And_Carry_Ids_Of_Messages()
    {
        var (client, cookie) = await SignInAsync(DovecotFixture.Bob);

        var (status, first) = await GetAsync(client, cookie, "/api/v1/folders/INBOX/messages?limit=5");
        var (_, second) = await GetAsync(client, cookie, $"/api/v1/folders/INBOX/messages?limit=5&after={first.GetProperty("next").GetString()}");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(5, first.GetProperty("items").GetArrayLength());
        Assert.True(first.GetProperty("total").GetInt32() >= dovecot.SeededMessageCount);
        var firstIds = first.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).ToList();
        var secondIds = second.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).ToList();
        Assert.Empty(firstIds.Intersect(secondIds));
        Assert.All(firstIds.Concat(secondIds), id => Assert.StartsWith("SU5CT1g.", id, StringComparison.Ordinal)); // base64url("INBOX")
    }

    [Fact]
    public async Task Previews_Come_With_The_List_And_Can_Be_Left_Out()
    {
        var (client, cookie) = await SignInAsync(DovecotFixture.Bob);

        var (_, with) = await GetAsync(client, cookie, "/api/v1/folders/INBOX/messages?limit=3&order=oldest");
        var (_, without) = await GetAsync(client, cookie, "/api/v1/folders/INBOX/messages?limit=3&order=oldest&preview=false");

        Assert.Equal("Hello, this is a plain message.", with.GetProperty("items")[0].GetProperty("preview").GetString());
        Assert.All(without.GetProperty("items").EnumerateArray(), i => Assert.Equal(string.Empty, i.GetProperty("preview").GetString()));
    }

    [Fact]
    public async Task The_Oldest_Order_Starts_With_The_First_Message()
    {
        var (client, cookie) = await SignInAsync(DovecotFixture.Bob);

        var (_, page) = await GetAsync(client, cookie, "/api/v1/folders/INBOX/messages?limit=2&order=oldest");

        Assert.Equal("Plain ASCII message", page.GetProperty("items")[0].GetProperty("subject").GetString());
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=101")]
    [InlineData("limit=abc")]
    [InlineData("after=garbage")]
    [InlineData("after=1.0")]
    [InlineData("order=sideways")]
    public async Task Wrong_Parameters_Are_A_400_With_A_Code(string query)
    {
        var (client, cookie) = await SignInAsync(DovecotFixture.Bob);

        var (status, body) = await GetAsync(client, cookie, $"/api/v1/folders/INBOX/messages?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task A_Missing_Folder_Is_404_Without_Its_Name()
    {
        var (client, cookie) = await SignInAsync(DovecotFixture.Bob);

        var (status, body) = await GetAsync(client, cookie, "/api/v1/folders/Secret%20plans/messages");

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("not_found", body.GetProperty("code").GetString());
        Assert.DoesNotContain("Secret", body.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Cursor_Of_Another_Folder_Version_Is_409()
    {
        var (client, cookie) = await SignInAsync(DovecotFixture.Bob);
        var (_, first) = await GetAsync(client, cookie, "/api/v1/folders/INBOX/messages?limit=2");
        var parts = first.GetProperty("next").GetString()!.Split('.');
        var stale = $"{uint.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture) + 1}.{parts[1]}";

        var (status, body) = await GetAsync(client, cookie, $"/api/v1/folders/INBOX/messages?after={stale}");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("uid_validity_changed", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_Folder_Path_With_Special_Characters_Is_Taken_From_The_Url()
    {
        var account = await dovecot.CreateAccountAsync($"route{Guid.NewGuid():N}"[..14], Token);
        await dovecot.CreateFolderAsync(account, "Работа", Token);
        await dovecot.CreateFolderAsync(account, "Работа.Отчёты", Token);
        await dovecot.SaveFixtureAsync(account, "Работа.Отчёты", "02-utf8-qp-alternative.eml", Token);
        var (client, cookie) = await SignInAsync(account);
        var (_, folders) = await GetAsync(client, cookie, "/api/v1/folders");
        var path = folders.GetProperty("folders").EnumerateArray().Single(f => f.GetProperty("name").GetString() == "Отчёты").GetProperty("path").GetString()!;

        var (status, page) = await GetAsync(client, cookie, $"/api/v1/folders/{Uri.EscapeDataString(path)}/messages");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Привет, мир", page.GetProperty("items")[0].GetProperty("subject").GetString());
    }

    [Fact]
    public async Task The_List_Needs_A_Session()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/folders/INBOX/messages", Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
