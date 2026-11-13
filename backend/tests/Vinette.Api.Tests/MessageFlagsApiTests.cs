using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Vinette.Api.Auth;
using Vinette.TestSupport;

namespace Vinette.Api.Tests;

/// <summary>Marking messages through the real application against a real Dovecot.</summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class MessageFlagsApiTests(DovecotFixture dovecot) : IDisposable
{
    private readonly WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseSetting("Imap:Host", dovecot.Host);
        b.UseSetting("Imap:Port", dovecot.ImapPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        b.UseSetting("Imap:Security", "None");
    });

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => factory.Dispose();

    /// <summary>A mailbox of its own with one unread message, signed in.</summary>
    private async Task<(HttpClient Client, string Cookie, string Id)> StartAsync()
    {
        var account = await dovecot.CreateAccountAsync($"flg{Guid.NewGuid():N}"[..14], Token);
        await dovecot.SaveFixtureAsync(account, "INBOX", "01-plain-ascii.eml", Token);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = await CsrfClient.PostJsonAsync(client, "/api/v1/auth/login", new { address = account.Address, password = account.Password, rememberMe = false }, Token);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(SessionCookie.Name + "=", StringComparison.Ordinal)).Split(';')[0];
        var list = await GetJsonAsync(client, cookie, "/api/v1/folders/INBOX/messages");
        return (client, cookie, list.GetProperty("items")[0].GetProperty("id").GetString()!);
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string cookie, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", cookie);
        var response = await client.SendAsync(request, Token);
        return JsonElement.Parse(await response.Content.ReadAsStringAsync(Token));
    }

    private static async Task<(HttpResponseMessage Response, JsonElement Body)> PatchAsync(HttpClient client, string cookie, string id, string json)
    {
        var sessionId = cookie[(SessionCookie.Name.Length + 1)..];
        var response = await CsrfClient.SendAsync(client, HttpMethod.Patch, $"/api/v1/messages/{id}/flags", Token, new StringContent(json, System.Text.Encoding.UTF8, "application/json"), sessionId);
        var text = await response.Content.ReadAsStringAsync(Token);
        return (response, text.Length == 0 ? default : JsonElement.Parse(text));
    }

    [Fact]
    public async Task A_Message_Is_Marked_As_Read_And_The_List_And_The_Message_Agree()
    {
        var (client, cookie, id) = await StartAsync();

        var (response, state) = await PatchAsync(client, cookie, id, "{\"seen\":true}");
        var list = await GetJsonAsync(client, cookie, "/api/v1/folders/INBOX/messages");
        var message = await GetJsonAsync(client, cookie, $"/api/v1/messages/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(state.GetProperty("seen").GetBoolean());
        Assert.False(state.GetProperty("flagged").GetBoolean());
        Assert.True(list.GetProperty("items")[0].GetProperty("seen").GetBoolean());
        Assert.True(message.GetProperty("seen").GetBoolean());
    }

    [Fact]
    public async Task The_Folder_Counter_Of_Unread_Messages_Follows()
    {
        var (client, cookie, id) = await StartAsync();
        var before = (await GetJsonAsync(client, cookie, "/api/v1/folders")).GetProperty("folders").EnumerateArray().Single(f => f.GetProperty("role").GetString() == "inbox");

        await PatchAsync(client, cookie, id, "{\"seen\":true}");
        var after = (await GetJsonAsync(client, cookie, "/api/v1/folders")).GetProperty("folders").EnumerateArray().Single(f => f.GetProperty("role").GetString() == "inbox");

        Assert.Equal(1, before.GetProperty("unread").GetInt32());
        Assert.Equal(0, after.GetProperty("unread").GetInt32());
    }

    [Fact]
    public async Task The_Flag_Is_Set_And_Removed_And_The_Flagged_Filter_Sees_It()
    {
        var (client, cookie, id) = await StartAsync();

        await PatchAsync(client, cookie, id, "{\"flagged\":true}");
        var flagged = await GetJsonAsync(client, cookie, "/api/v1/folders/INBOX/messages?filter=flagged");
        var (_, removed) = await PatchAsync(client, cookie, id, "{\"flagged\":false}");
        var none = await GetJsonAsync(client, cookie, "/api/v1/folders/INBOX/messages?filter=flagged");

        Assert.Equal(1, flagged.GetProperty("items").GetArrayLength());
        Assert.False(removed.GetProperty("flagged").GetBoolean());
        Assert.Equal(0, none.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Repeating_A_Change_Gives_The_Same_Answer()
    {
        var (client, cookie, id) = await StartAsync();

        var (_, first) = await PatchAsync(client, cookie, id, "{\"seen\":true,\"flagged\":true}");
        var (second, again) = await PatchAsync(client, cookie, id, "{\"seen\":true,\"flagged\":true}");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(first.GetRawText(), again.GetRawText());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"seen\":null}")]
    [InlineData("null")]
    [InlineData("{\"seen\":\"yes\"}")]
    [InlineData("{\"flagged\":1}")]
    public async Task A_Body_That_Changes_Nothing_Or_Is_Wrong_Is_A_400_With_A_Code(string body)
    {
        var (client, cookie, id) = await StartAsync();

        var (response, problem) = await PatchAsync(client, cookie, id, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task A_Message_That_Does_Not_Exist_Is_404_And_Another_Folder_Version_409()
    {
        var (client, cookie, id) = await StartAsync();
        var parts = id.Split('.');

        var (gone, goneBody) = await PatchAsync(client, cookie, $"{parts[0]}.{parts[1]}.99999", "{\"seen\":true}");
        var (stale, staleBody) = await PatchAsync(client, cookie, $"{parts[0]}.{uint.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) + 1}.{parts[2]}", "{\"seen\":true}");

        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal("not_found", goneBody.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("uid_validity_changed", staleBody.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Without_The_Anti_Forgery_Token_Nothing_Changes()
    {
        var (client, cookie, id) = await StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/messages/{id}/flags") { Content = JsonContent.Create(new { seen = true }) };
        request.Headers.Add("Origin", CsrfClient.Origin);
        request.Headers.Add("Cookie", cookie);

        var response = await client.SendAsync(request, Token);
        var message = await GetJsonAsync(client, cookie, $"/api/v1/messages/{id}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(message.GetProperty("seen").GetBoolean());
    }

    [Fact]
    public async Task Marking_Needs_A_Session()
    {
        var response = await CsrfClient.SendAsync(factory.CreateClient(), HttpMethod.Patch, "/api/v1/messages/SU5CT1g.1.1/flags", Token, JsonContent.Create(new { seen = true }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_User_Cannot_Mark_Another_Users_Message()
    {
        var (ownerClient, ownerCookie, id) = await StartAsync();
        var other = await dovecot.CreateAccountAsync($"oth{Guid.NewGuid():N}"[..14], Token);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = await CsrfClient.PostJsonAsync(client, "/api/v1/auth/login", new { address = other.Address, password = other.Password, rememberMe = false }, Token);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(SessionCookie.Name + "=", StringComparison.Ordinal)).Split(';')[0];

        var (response, _) = await PatchAsync(client, cookie, id, "{\"seen\":true}");

        // The id belongs to another mailbox: no such message here (404), or this INBOX is another incarnation (409).
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.Conflict });
        Assert.False((await GetJsonAsync(ownerClient, ownerCookie, $"/api/v1/messages/{id}")).GetProperty("seen").GetBoolean()); // the owner's message is untouched
    }
}
