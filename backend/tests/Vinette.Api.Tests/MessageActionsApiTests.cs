using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Vinette.Api.Auth;
using Vinette.TestSupport;

namespace Vinette.Api.Tests;

/// <summary>Archive, spam, trash, move and delete for good through the real application against a real Dovecot.</summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class MessageActionsApiTests(DovecotFixture dovecot) : IDisposable
{
    private readonly WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseSetting("Imap:Host", dovecot.Host);
        b.UseSetting("Imap:Port", dovecot.ImapPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        b.UseSetting("Imap:Security", "None");
    });

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => factory.Dispose();

    private sealed record Client(HttpClient Http, string Session)
    {
        public async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string url)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", $"{SessionCookie.Name}={Session}");
            var response = await Http.SendAsync(request, Token);
            return (response.StatusCode, Parse(await response.Content.ReadAsStringAsync(Token)));
        }

        public async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod method, string url, object? body = null)
        {
            var response = await CsrfClient.SendAsync(Http, method, url, Token, body is null ? null : JsonContent.Create(body), Session);
            return (response.StatusCode, Parse(await response.Content.ReadAsStringAsync(Token)));
        }

        /// <summary>The ids of the messages of a folder, oldest first.</summary>
        public async Task<List<string>> IdsAsync(string folder)
        {
            var (_, page) = await GetAsync($"/api/v1/folders/{Uri.EscapeDataString(folder)}/messages?preview=false&limit=100&newestFirst=false");
            return [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!)];
        }

        private static JsonElement Parse(string text) => text.Length == 0 ? default : JsonElement.Parse(text);
    }

    private async Task<Client> StartAsync(int messages, params string[] folders)
    {
        var account = await dovecot.CreateAccountAsync($"act{Guid.NewGuid():N}"[..14], Token);
        for (var i = 0; i < messages; i++)
        {
            await dovecot.SaveFixtureAsync(account, "INBOX", i % 2 == 0 ? "01-plain-ascii.eml" : "02-utf8-qp-alternative.eml", Token);
        }

        foreach (var folder in folders)
        {
            await dovecot.CreateFolderAsync(account, folder, Token);
        }

        var http = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = await CsrfClient.PostJsonAsync(http, "/api/v1/auth/login", new { address = account.Address, password = account.Password, rememberMe = false }, Token);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(SessionCookie.Name + "=", StringComparison.Ordinal));
        return new Client(http, cookie[(SessionCookie.Name.Length + 1)..].Split(';')[0]);
    }

    [Theory]
    [InlineData("spam", "Junk")]
    [InlineData("trash", "Trash")]
    public async Task A_Message_Goes_To_The_Folder_Of_The_Action_And_Is_Found_There_By_The_Answer(string action, string folder)
    {
        var client = await StartAsync(2);
        var ids = await client.IdsAsync("INBOX");

        var (status, moved) = await client.SendAsync(HttpMethod.Post, $"/api/v1/messages/{ids[0]}/{action}");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(folder, moved.GetProperty("folder").GetString());
        Assert.Equal(ids[1], Assert.Single(await client.IdsAsync("INBOX")));
        var newId = Assert.Single(await client.IdsAsync(folder));
        Assert.Equal(newId, moved.GetProperty("id").GetString());
        var (opened, _) = await client.GetAsync($"/api/v1/messages/{newId}");
        Assert.Equal(HttpStatusCode.OK, opened);
        var (gone, _) = await client.GetAsync($"/api/v1/messages/{ids[0]}");
        Assert.Equal(HttpStatusCode.NotFound, gone); // by the old id it is no longer there
    }

    [Fact]
    public async Task Without_An_Archive_Folder_The_Action_Says_So_And_Changes_Nothing()
    {
        var client = await StartAsync(1);
        var ids = await client.IdsAsync("INBOX");

        var (status, problem) = await client.SendAsync(HttpMethod.Post, $"/api/v1/messages/{ids[0]}/archive");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("no_target_folder", problem.GetProperty("code").GetString());
        Assert.Single(await client.IdsAsync("INBOX"));
    }

    [Fact]
    public async Task With_An_Archive_Folder_A_Message_Is_Archived()
    {
        var client = await StartAsync(1, "Archive");
        var ids = await client.IdsAsync("INBOX");

        var (status, moved) = await client.SendAsync(HttpMethod.Post, $"/api/v1/messages/{ids[0]}/archive");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Archive", moved.GetProperty("folder").GetString());
        Assert.Empty(await client.IdsAsync("INBOX"));
        Assert.Single(await client.IdsAsync("Archive"));
    }

    [Fact]
    public async Task A_Message_Can_Be_Moved_Anywhere_And_Brought_Back_With_The_Id_From_The_Answer()
    {
        var client = await StartAsync(1, "Work");
        var ids = await client.IdsAsync("INBOX");

        var (_, there) = await client.SendAsync(HttpMethod.Post, $"/api/v1/messages/{ids[0]}/move", new { to = "Work" });
        var (status, back) = await client.SendAsync(HttpMethod.Post, $"/api/v1/messages/{there.GetProperty("id").GetString()}/move", new { to = "INBOX" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("INBOX", back.GetProperty("folder").GetString());
        Assert.Single(await client.IdsAsync("INBOX"));
        Assert.Empty(await client.IdsAsync("Work"));
    }

    [Fact]
    public async Task Moving_To_A_Folder_That_Is_Not_There_Is_Not_Found_And_Changes_Nothing()
    {
        var client = await StartAsync(1);
        var ids = await client.IdsAsync("INBOX");

        var (status, problem) = await client.SendAsync(HttpMethod.Post, $"/api/v1/messages/{ids[0]}/move", new { to = "Nowhere" });

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("not_found", problem.GetProperty("code").GetString());
        Assert.DoesNotContain("Nowhere", problem.ToString(), StringComparison.Ordinal); // the user's folder names stay out of answers
        Assert.Single(await client.IdsAsync("INBOX"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"to\": \"\"}")]
    [InlineData("{\"to\": \"   \"}")]
    [InlineData("{\"to\": \"INBOX\"}")] // where it is already
    [InlineData("{\"to\": \"inbox\"}")]
    public async Task A_Move_Without_A_Place_To_Go_Is_Refused(string body)
    {
        var client = await StartAsync(1);
        var ids = await client.IdsAsync("INBOX");

        var response = await CsrfClient.SendAsync(client.Http, HttpMethod.Post, $"/api/v1/messages/{ids[0]}/move", Token, new StringContent(body, System.Text.Encoding.UTF8, "application/json"), client.Session);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single(await client.IdsAsync("INBOX"));
    }

    [Fact]
    public async Task Trashing_A_Message_That_Is_Already_In_The_Trash_Is_Refused()
    {
        var client = await StartAsync(1);
        var (_, moved) = await client.SendAsync(HttpMethod.Post, $"/api/v1/messages/{(await client.IdsAsync("INBOX"))[0]}/trash");

        var (status, _) = await client.SendAsync(HttpMethod.Post, $"/api/v1/messages/{moved.GetProperty("id").GetString()}/trash");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Single(await client.IdsAsync("Trash"));
    }

    [Fact]
    public async Task A_Message_In_The_Trash_Is_Deleted_For_Good()
    {
        var client = await StartAsync(2);
        var (_, moved) = await client.SendAsync(HttpMethod.Post, $"/api/v1/messages/{(await client.IdsAsync("INBOX"))[0]}/trash");

        var (status, _) = await client.SendAsync(HttpMethod.Delete, $"/api/v1/messages/{moved.GetProperty("id").GetString()}");

        Assert.Equal(HttpStatusCode.NoContent, status);
        Assert.Empty(await client.IdsAsync("Trash"));
        Assert.Single(await client.IdsAsync("INBOX"));
    }

    [Fact]
    public async Task A_Message_Outside_The_Trash_Cannot_Be_Deleted_For_Good()
    {
        var client = await StartAsync(1);
        var ids = await client.IdsAsync("INBOX");

        var (status, problem) = await client.SendAsync(HttpMethod.Delete, $"/api/v1/messages/{ids[0]}");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("not_in_trash", problem.GetProperty("code").GetString());
        Assert.Single(await client.IdsAsync("INBOX"));
    }

    [Fact]
    public async Task A_Message_That_Is_Gone_Is_Not_Found_By_Every_Action()
    {
        var client = await StartAsync(1);
        var id = (await client.IdsAsync("INBOX"))[0];
        await client.SendAsync(HttpMethod.Post, $"/api/v1/messages/{id}/trash");

        foreach (var action in new[] { "trash", "spam", "move" })
        {
            var (status, _) = await client.SendAsync(HttpMethod.Post, $"/api/v1/messages/{id}/{action}", action == "move" ? new { to = "Junk" } : null);
            Assert.Equal(HttpStatusCode.NotFound, status);
        }
    }

    [Fact]
    public async Task The_Actions_Need_A_Session_And_The_Anti_Forgery_Token()
    {
        var client = await StartAsync(1);
        var id = (await client.IdsAsync("INBOX"))[0];

        using var anonymous = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/messages/{id}/trash");
        var noSession = await client.Http.SendAsync(anonymous, Token);
        using var withoutToken = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/messages/{id}");
        withoutToken.Headers.Add("Cookie", $"{SessionCookie.Name}={client.Session}");
        var noToken = await client.Http.SendAsync(withoutToken, Token);

        Assert.True(noSession.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
        Assert.Equal(HttpStatusCode.Forbidden, noToken.StatusCode);
        Assert.Single(await client.IdsAsync("INBOX"));
    }
}
