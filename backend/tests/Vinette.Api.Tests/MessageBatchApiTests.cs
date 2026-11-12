#pragma warning disable CA1861 // Small literal lists as test data: a shared static field per list would only make the tests harder to read.
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Vinette.Api.Auth;
using Vinette.TestSupport;

namespace Vinette.Api.Tests;

/// <summary>Actions on several messages at once through the real application against a real Dovecot.</summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class MessageBatchApiTests(DovecotFixture dovecot) : IDisposable
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
        public async Task<JsonElement> GetAsync(string url)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Cookie", $"{SessionCookie.Name}={Session}");
            var response = await Http.SendAsync(request, Token);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token)).RootElement;
        }

        public async Task<(HttpStatusCode Status, JsonElement Body)> BatchAsync(object body)
        {
            var response = await CsrfClient.SendAsync(Http, HttpMethod.Post, "/api/v1/messages/batch", Token, JsonContent.Create(body), Session);
            var text = await response.Content.ReadAsStringAsync(Token);
            return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement);
        }

        public async Task<List<JsonElement>> ItemsAsync(string folder) =>
            [.. (await GetAsync($"/api/v1/folders/{Uri.EscapeDataString(folder)}/messages?preview=false&limit=100&newestFirst=false")).GetProperty("items").EnumerateArray()];

        public async Task<List<string>> IdsAsync(string folder) => [.. (await ItemsAsync(folder)).Select(i => i.GetProperty("id").GetString()!)];
    }

    private async Task<Client> StartAsync(int messages, params string[] folders)
    {
        var account = await dovecot.CreateAccountAsync($"bat{Guid.NewGuid():N}"[..14], Token);
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
    [InlineData("trash", "Trash")]
    [InlineData("spam", "Junk")]
    [InlineData("archive", "Archive")]
    public async Task Selected_Messages_Go_To_The_Folder_Of_The_Action_And_Each_Can_Be_Found_There(string action, string folder)
    {
        var client = await StartAsync(3, "Archive");
        var ids = await client.IdsAsync("INBOX");

        var (status, result) = await client.BatchAsync(new { ids = new[] { ids[0], ids[2] }, action });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, result.GetProperty("changed").GetInt32());
        Assert.Equal(folder, result.GetProperty("folder").GetString());
        Assert.Equal([ids[1]], await client.IdsAsync("INBOX"));
        var there = await client.IdsAsync(folder);
        Assert.Equal(there.Order(), result.GetProperty("moved").EnumerateArray().Select(m => m.GetProperty("id").GetString()!).Order());
        Assert.Equal(new[] { ids[0], ids[2] }.Order(), result.GetProperty("moved").EnumerateArray().Select(m => m.GetProperty("from").GetString()!).Order());
    }

    [Fact]
    public async Task Selected_Messages_Move_To_A_Named_Folder_And_Back()
    {
        var client = await StartAsync(2, "Work");
        var ids = await client.IdsAsync("INBOX");

        var (_, there) = await client.BatchAsync(new { ids, action = "move", to = "Work" });
        var newIds = there.GetProperty("moved").EnumerateArray().Select(m => m.GetProperty("id").GetString()!).ToArray();
        var (status, back) = await client.BatchAsync(new { ids = newIds, action = "move", to = "INBOX" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, back.GetProperty("changed").GetInt32());
        Assert.Equal(2, (await client.IdsAsync("INBOX")).Count);
        Assert.Empty(await client.IdsAsync("Work"));
    }

    [Theory]
    [InlineData("read", "seen", true)]
    [InlineData("flag", "flagged", true)]
    public async Task Selected_Messages_Are_Marked_And_Unmarked_At_Once(string action, string property, bool value)
    {
        var client = await StartAsync(3);
        var ids = await client.IdsAsync("INBOX");

        var (status, result) = await client.BatchAsync(new { ids = new[] { ids[0], ids[1] }, action });
        var marked = await client.ItemsAsync("INBOX");
        var (_, undone) = await client.BatchAsync(new { ids = new[] { ids[0], ids[1] }, action = "un" + action });
        var unmarked = await client.ItemsAsync("INBOX");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, result.GetProperty("changed").GetInt32());
        Assert.Equal([value, value, !value], marked.Select(m => m.GetProperty(property).GetBoolean()));
        Assert.Equal(2, undone.GetProperty("changed").GetInt32());
        Assert.All(unmarked, m => Assert.Equal(!value, m.GetProperty(property).GetBoolean()));
    }

    [Fact]
    public async Task Messages_That_Are_Gone_Are_Left_Out_And_Counted_Out()
    {
        var client = await StartAsync(2);
        var ids = await client.IdsAsync("INBOX");
        await client.BatchAsync(new { ids = new[] { ids[0] }, action = "trash" });

        var (status, result) = await client.BatchAsync(new { ids, action = "spam" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, result.GetProperty("changed").GetInt32());
        Assert.Single(await client.IdsAsync("Junk"));
    }

    [Fact]
    public async Task A_Batch_Of_More_Than_A_Hundred_Is_Refused_And_Nothing_Changes()
    {
        var client = await StartAsync(1);
        var id = (await client.IdsAsync("INBOX"))[0];
        var parts = id.Split('.');
        var many = Enumerable.Range(1, 101).Select(i => $"{parts[0]}.{parts[1]}.{i}").ToArray();

        var (status, problem) = await client.BatchAsync(new { ids = many, action = "trash" });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("100", problem.GetProperty("title").GetString(), StringComparison.Ordinal);
        Assert.Single(await client.IdsAsync("INBOX"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"ids\": [], \"action\": \"trash\"}")]
    [InlineData("{\"ids\": [\"not-an-id\"], \"action\": \"trash\"}")]
    [InlineData("{\"ids\": [\"SU5CT1g.1.1\", \"V29yaw.1.1\"], \"action\": \"trash\"}")] // two folders
    [InlineData("{\"ids\": [\"SU5CT1g.1.1\", \"SU5CT1g.1.1\"], \"action\": \"trash\"}")] // twice
    [InlineData("{\"ids\": [\"SU5CT1g.1.1\"], \"action\": \"explode\"}")]
    [InlineData("{\"ids\": [\"SU5CT1g.1.1\"], \"action\": \"move\"}")] // no target
    [InlineData("{\"ids\": [\"SU5CT1g.1.1\"], \"action\": \"move\", \"to\": \"inbox\"}")] // where they are
    public async Task A_Batch_That_Makes_No_Sense_Is_Refused(string body)
    {
        var client = await StartAsync(1);

        var response = await CsrfClient.SendAsync(client.Http, HttpMethod.Post, "/api/v1/messages/batch", Token, new StringContent(body, System.Text.Encoding.UTF8, "application/json"), client.Session);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Single(await client.IdsAsync("INBOX"));
    }

    [Fact]
    public async Task Without_An_Archive_Folder_The_Batch_Says_So()
    {
        var client = await StartAsync(1);
        var ids = await client.IdsAsync("INBOX");

        var (status, problem) = await client.BatchAsync(new { ids, action = "archive" });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("no_target_folder", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_Batch_Needs_The_Anti_Forgery_Token()
    {
        var client = await StartAsync(1);
        var ids = await client.IdsAsync("INBOX");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/messages/batch") { Content = JsonContent.Create(new { ids, action = "trash" }) };
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={client.Session}");
        var response = await client.Http.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Single(await client.IdsAsync("INBOX"));
    }
}
