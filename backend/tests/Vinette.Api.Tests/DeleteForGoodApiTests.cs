using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Vinette.Api.Auth;
using Vinette.TestSupport;

namespace Vinette.Api.Tests;

/// <summary>Deleting for good: selected messages of the trash, and emptying the trash or the spam folder, against a real Dovecot.</summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class DeleteForGoodApiTests(DovecotFixture dovecot) : IDisposable
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
        public async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpMethod method, string url, object? body = null)
        {
            var response = await CsrfClient.SendAsync(Http, method, url, Token, body is null ? null : JsonContent.Create(body), Session);
            var text = await response.Content.ReadAsStringAsync(Token);
            return (response.StatusCode, text.Length == 0 ? default : JsonElement.Parse(text));
        }

        public async Task<List<string>> IdsAsync(string folder)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/folders/{Uri.EscapeDataString(folder)}/messages?preview=false&limit=100&newestFirst=false");
            request.Headers.Add("Cookie", $"{SessionCookie.Name}={Session}");
            var response = await Http.SendAsync(request, Token);
            var page = JsonElement.Parse(await response.Content.ReadAsStringAsync(Token));
            return [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!)];
        }

        /// <summary>Moves every message of the inbox to a folder of the action (trash, spam) and returns their new ids.</summary>
        public async Task<List<string>> ThrowAwayAsync(string action, int count)
        {
            var ids = (await IdsAsync("INBOX")).Take(count).ToList();
            var (_, result) = await SendAsync(HttpMethod.Post, "/api/v1/messages/batch", new { ids, action });
            return [.. result.GetProperty("moved").EnumerateArray().Select(m => m.GetProperty("id").GetString()!)];
        }
    }

    private async Task<Client> StartAsync(int messages)
    {
        var account = await dovecot.CreateAccountAsync($"del{Guid.NewGuid():N}"[..14], Token);
        for (var i = 0; i < messages; i++)
        {
            await dovecot.SaveFixtureAsync(account, "INBOX", i % 2 == 0 ? "01-plain-ascii.eml" : "02-utf8-qp-alternative.eml", Token);
        }

        var http = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = await CsrfClient.PostJsonAsync(http, "/api/v1/auth/login", new { address = account.Address, password = account.Password, rememberMe = false }, Token);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(SessionCookie.Name + "=", StringComparison.Ordinal));
        return new Client(http, cookie[(SessionCookie.Name.Length + 1)..].Split(';')[0]);
    }

    [Fact]
    public async Task Selected_Messages_Of_The_Trash_Are_Deleted_For_Good_And_The_Others_Stay()
    {
        var client = await StartAsync(3);
        var inTrash = await client.ThrowAwayAsync("trash", 3);

        var (status, result) = await client.SendAsync(HttpMethod.Post, "/api/v1/messages/batch", new { ids = new[] { inTrash[0], inTrash[2] }, action = "delete" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, result.GetProperty("changed").GetInt32());
        Assert.Equal([inTrash[1]], await client.IdsAsync("Trash"));
    }

    [Fact]
    public async Task Messages_Outside_The_Trash_Cannot_Be_Deleted_For_Good_Not_Even_In_The_Spam_Folder()
    {
        var client = await StartAsync(2);
        var inbox = await client.IdsAsync("INBOX");
        var inJunk = await client.ThrowAwayAsync("spam", 1);

        var (fromInbox, problem) = await client.SendAsync(HttpMethod.Post, "/api/v1/messages/batch", new { ids = new[] { inbox[1] }, action = "delete" });
        var (fromJunk, _) = await client.SendAsync(HttpMethod.Post, "/api/v1/messages/batch", new { ids = inJunk, action = "delete" });

        Assert.Equal(HttpStatusCode.Conflict, fromInbox);
        Assert.Equal("not_in_trash", problem.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Conflict, fromJunk);
        Assert.Single(await client.IdsAsync("INBOX"));
        Assert.Single(await client.IdsAsync("Junk"));
    }

    [Fact]
    public async Task Messages_Already_Gone_Are_Not_Counted()
    {
        var client = await StartAsync(2);
        var inTrash = await client.ThrowAwayAsync("trash", 2);
        await client.SendAsync(HttpMethod.Post, "/api/v1/messages/batch", new { ids = new[] { inTrash[0] }, action = "delete" });

        var (status, result) = await client.SendAsync(HttpMethod.Post, "/api/v1/messages/batch", new { ids = inTrash, action = "delete" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, result.GetProperty("changed").GetInt32());
        Assert.Empty(await client.IdsAsync("Trash"));
    }

    [Theory]
    [InlineData("trash", "Trash")]
    [InlineData("spam", "Junk")]
    public async Task The_Trash_And_The_Spam_Folder_Can_Be_Emptied(string action, string folder)
    {
        var client = await StartAsync(3);
        await client.ThrowAwayAsync(action, 3);

        var (status, result) = await client.SendAsync(HttpMethod.Post, $"/api/v1/folders/{folder}/empty");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(3, result.GetProperty("deleted").GetInt32());
        Assert.Empty(await client.IdsAsync(folder));
    }

    [Fact]
    public async Task Emptying_An_Empty_Folder_Deletes_Nothing()
    {
        var client = await StartAsync(1);
        await client.ThrowAwayAsync("trash", 1);
        await client.SendAsync(HttpMethod.Post, "/api/v1/folders/Trash/empty");

        var (status, result) = await client.SendAsync(HttpMethod.Post, "/api/v1/folders/Trash/empty");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, result.GetProperty("deleted").GetInt32());
    }

    [Theory]
    [InlineData("INBOX")]
    [InlineData("inbox")]
    [InlineData("Nowhere")]
    public async Task No_Other_Folder_Can_Be_Emptied(string folder)
    {
        var client = await StartAsync(2);

        var (status, problem) = await client.SendAsync(HttpMethod.Post, $"/api/v1/folders/{folder}/empty");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("not_in_trash", problem.GetProperty("code").GetString());
        Assert.Equal(2, (await client.IdsAsync("INBOX")).Count);
    }

    [Fact]
    public async Task Emptying_Needs_A_Session_And_The_Anti_Forgery_Token()
    {
        var client = await StartAsync(1);
        await client.ThrowAwayAsync("trash", 1);

        using var anonymous = new HttpRequestMessage(HttpMethod.Post, "/api/v1/folders/Trash/empty");
        var noSession = await client.Http.SendAsync(anonymous, Token);
        using var withoutToken = new HttpRequestMessage(HttpMethod.Post, "/api/v1/folders/Trash/empty");
        withoutToken.Headers.Add("Cookie", $"{SessionCookie.Name}={client.Session}");
        var noToken = await client.Http.SendAsync(withoutToken, Token);

        Assert.True(noSession.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
        Assert.Equal(HttpStatusCode.Forbidden, noToken.StatusCode);
        Assert.Single(await client.IdsAsync("Trash"));
    }
}
