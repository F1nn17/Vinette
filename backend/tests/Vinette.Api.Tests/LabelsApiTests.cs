#pragma warning disable CA1861 // Small literal lists as test data: a shared static field per list would only make the tests harder to read.
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Vinette.Api.Auth;
using Vinette.TestSupport;

namespace Vinette.Api.Tests;

/// <summary>Labels through the real application against a real Dovecot: create, put on messages, rename, color, delete.</summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class LabelsApiTests(DovecotFixture dovecot) : IDisposable
{
    private readonly string settingsDirectory = Path.Combine(Path.GetTempPath(), "vinette-labels-" + Guid.NewGuid().ToString("N"));
    private readonly List<WebApplicationFactory<Program>> factories = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        factories.ForEach(f => f.Dispose());
        if (Directory.Exists(settingsDirectory))
        {
            Directory.Delete(settingsDirectory, recursive: true);
        }
    }

    private sealed record Client(HttpClient Http, string Session, MailAccount Account)
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

        public async Task<List<string>> NamesAsync() =>
            [.. (await GetAsync("/api/v1/labels")).Body.GetProperty("labels").EnumerateArray().Select(l => l.GetProperty("name").GetString()!)];

        public async Task<List<(string Id, List<string> Labels)>> MessagesAsync(string folder = "INBOX")
        {
            var (_, page) = await GetAsync($"/api/v1/folders/{Uri.EscapeDataString(folder)}/messages?preview=false&limit=100&newestFirst=false");
            return [.. page.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("id").GetString()!, i.GetProperty("labels").EnumerateArray().Select(l => l.GetString()!).ToList()))];
        }

        private static JsonElement Parse(string text) => text.Length == 0 ? default : JsonDocument.Parse(text).RootElement;
    }

    private async Task<Client> StartAsync(int messages, params string[] folders)
    {
        var web = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Imap:Host", dovecot.Host);
            b.UseSetting("Imap:Port", dovecot.ImapPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
            b.UseSetting("Imap:Security", "None");
            b.UseSetting("Settings:Path", settingsDirectory);
        });
        factories.Add(web);
        var account = await dovecot.CreateAccountAsync($"lab{Guid.NewGuid():N}"[..14], Token);
        for (var i = 0; i < messages; i++)
        {
            await dovecot.SaveFixtureAsync(account, "INBOX", i % 2 == 0 ? "01-plain-ascii.eml" : "02-utf8-qp-alternative.eml", Token);
        }

        foreach (var folder in folders)
        {
            await dovecot.CreateFolderAsync(account, folder, Token);
        }

        var http = web.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = await CsrfClient.PostJsonAsync(http, "/api/v1/auth/login", new { address = account.Address, password = account.Password, rememberMe = false }, Token);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(SessionCookie.Name + "=", StringComparison.Ordinal));
        return new Client(http, cookie[(SessionCookie.Name.Length + 1)..].Split(';')[0], account);
    }

    [Fact]
    public async Task A_New_Mailbox_Has_No_Labels()
    {
        var client = await StartAsync(0);

        Assert.Empty(await client.NamesAsync());
    }

    [Fact]
    public async Task A_Label_Is_Created_With_A_Color_Before_Any_Message_Has_It()
    {
        var client = await StartAsync(0);

        var (status, created) = await client.SendAsync(HttpMethod.Post, "/api/v1/labels", new { name = "  Счета 2026 ", color = "bills" });
        var (_, all) = await client.GetAsync("/api/v1/labels");

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("Счета 2026", created.GetProperty("name").GetString());
        var label = Assert.Single(all.GetProperty("labels").EnumerateArray());
        Assert.Equal("Счета 2026", label.GetProperty("name").GetString());
        Assert.Equal("bills", label.GetProperty("color").GetString());
    }

    [Fact]
    public async Task A_Label_Without_A_Color_Has_None()
    {
        var client = await StartAsync(0);

        await client.SendAsync(HttpMethod.Post, "/api/v1/labels", new { name = "Plain" });
        var (_, all) = await client.GetAsync("/api/v1/labels");

        Assert.Equal(JsonValueKind.Null, Assert.Single(all.GetProperty("labels").EnumerateArray()).GetProperty("color").ValueKind);
    }

    [Theory]
    [InlineData("Work", "WORK")]
    [InlineData("Работа", "работа")]
    public async Task A_Name_That_Is_Taken_Is_Refused_Whatever_The_Case(string first, string second)
    {
        var client = await StartAsync(0);
        await client.SendAsync(HttpMethod.Post, "/api/v1/labels", new { name = first });

        var (status, problem) = await client.SendAsync(HttpMethod.Post, "/api/v1/labels", new { name = second });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("conflict", problem.GetProperty("code").GetString());
        Assert.Single(await client.NamesAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("tab\there")]
    [InlineData("ЯЯЯЯЯЯЯЯЯЯЯЯЯЯЯ")] // 15 letters: too long for the code the server keeps
    public async Task A_Name_That_Cannot_Be_A_Label_Is_Refused(string name)
    {
        var client = await StartAsync(0);

        var (status, problem) = await client.SendAsync(HttpMethod.Post, "/api/v1/labels", new { name });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("bad_request", problem.GetProperty("code").GetString());
        Assert.Empty(await client.NamesAsync());
    }

    [Fact]
    public async Task A_Color_Outside_The_Palette_Is_Refused()
    {
        var client = await StartAsync(0);

        var (status, _) = await client.SendAsync(HttpMethod.Post, "/api/v1/labels", new { name = "Work", color = "#ff0000" });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Empty(await client.NamesAsync());
    }

    [Fact]
    public async Task A_Message_Gets_And_Loses_Labels_And_A_New_Name_Becomes_A_Label_Of_The_Mailbox()
    {
        var client = await StartAsync(2);
        var ids = (await client.MessagesAsync()).Select(m => m.Id).ToList();

        var (status, put) = await client.SendAsync(HttpMethod.Patch, $"/api/v1/messages/{ids[0]}/labels", new { add = new[] { "Работа", "Work" } });
        var inList = (await client.MessagesAsync())[0].Labels;
        var (_, opened) = await client.GetAsync($"/api/v1/messages/{ids[0]}");
        var (_, removed) = await client.SendAsync(HttpMethod.Patch, $"/api/v1/messages/{ids[0]}/labels", new { remove = new[] { "Work" } });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["Work", "Работа"], put.GetProperty("labels").EnumerateArray().Select(l => l.GetString()).Order(StringComparer.Ordinal));
        Assert.Equal(["Work", "Работа"], inList.Order(StringComparer.Ordinal)); // visible in the list ...
        Assert.Equal(["Work", "Работа"], opened.GetProperty("labels").EnumerateArray().Select(l => l.GetString()).Order(StringComparer.Ordinal)); // ... and in the message
        Assert.Equal(["Работа"], removed.GetProperty("labels").EnumerateArray().Select(l => l.GetString()));
        Assert.Equal(["Work", "Работа"], (await client.NamesAsync()).Order(StringComparer.Ordinal)); // the list of labels keeps what was put on
        Assert.Empty((await client.MessagesAsync())[1].Labels);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"add\": []}")]
    [InlineData("{\"add\": [\"Work\"], \"remove\": [\"work\"]}")]
    [InlineData("{\"add\": [\"a/b\"]}")]
    [InlineData("{\"add\": [\"\"]}")]
    public async Task A_Change_Of_Labels_That_Makes_No_Sense_Is_Refused(string body)
    {
        var client = await StartAsync(1);
        var id = (await client.MessagesAsync())[0].Id;

        var response = await CsrfClient.SendAsync(client.Http, HttpMethod.Patch, $"/api/v1/messages/{id}/labels", Token, new StringContent(body, System.Text.Encoding.UTF8, "application/json"), client.Session);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await client.MessagesAsync())[0].Labels);
    }

    [Fact]
    public async Task Too_Many_Labels_At_Once_Are_Refused()
    {
        var client = await StartAsync(1);
        var id = (await client.MessagesAsync())[0].Id;

        var (status, _) = await client.SendAsync(HttpMethod.Patch, $"/api/v1/messages/{id}/labels", new { add = Enumerable.Range(0, 21).Select(i => $"l{i}") });

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Labels_Of_A_Message_That_Is_Gone_Are_Not_Found()
    {
        var client = await StartAsync(1);
        var id = (await client.MessagesAsync())[0].Id;
        await client.SendAsync(HttpMethod.Post, $"/api/v1/messages/{id}/trash");

        var (status, _) = await client.SendAsync(HttpMethod.Patch, $"/api/v1/messages/{id}/labels", new { add = new[] { "Work" } });

        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Fact]
    public async Task A_Label_Is_Renamed_On_Its_Messages_And_Keeps_Its_Color()
    {
        var client = await StartAsync(2, "Work");
        var ids = (await client.MessagesAsync()).Select(m => m.Id).ToList();
        await client.SendAsync(HttpMethod.Post, "/api/v1/labels", new { name = "Работа", color = "work" });
        await client.SendAsync(HttpMethod.Patch, $"/api/v1/messages/{ids[0]}/labels", new { add = new[] { "Работа" } });
        var (_, moved) = await client.SendAsync(HttpMethod.Post, $"/api/v1/messages/{ids[1]}/move", new { to = "Work" });
        await client.SendAsync(HttpMethod.Patch, $"/api/v1/messages/{moved.GetProperty("id").GetString()}/labels", new { add = new[] { "Работа" } });

        var (status, renamed) = await client.SendAsync(HttpMethod.Patch, "/api/v1/labels/" + Uri.EscapeDataString("Работа"), new { name = "Job 2026" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Job 2026", renamed.GetProperty("name").GetString());
        Assert.Equal("work", renamed.GetProperty("color").GetString());
        Assert.Equal(["Job 2026"], (await client.MessagesAsync())[0].Labels);
        Assert.Equal(["Job 2026"], (await client.MessagesAsync("Work"))[0].Labels);
        Assert.Equal(["Job 2026"], await client.NamesAsync());
    }

    [Fact]
    public async Task A_Label_Cannot_Be_Renamed_To_A_Name_That_Is_Taken()
    {
        var client = await StartAsync(0);
        await client.SendAsync(HttpMethod.Post, "/api/v1/labels", new { name = "One" });
        await client.SendAsync(HttpMethod.Post, "/api/v1/labels", new { name = "Two" });

        var (status, _) = await client.SendAsync(HttpMethod.Patch, "/api/v1/labels/One", new { name = "two" });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(["One", "Two"], await client.NamesAsync());
    }

    [Fact]
    public async Task The_Color_Of_A_Label_Is_Changed_And_Taken_Away()
    {
        var client = await StartAsync(0);
        await client.SendAsync(HttpMethod.Post, "/api/v1/labels", new { name = "Work", color = "work" });

        var (_, personal) = await client.SendAsync(HttpMethod.Patch, "/api/v1/labels/Work", new { color = "personal" });
        var (_, none) = await client.SendAsync(HttpMethod.Patch, "/api/v1/labels/WORK", new { color = string.Empty });
        var (status, _) = await client.SendAsync(HttpMethod.Patch, "/api/v1/labels/Work", new { color = "purple" });

        Assert.Equal("personal", personal.GetProperty("color").GetString());
        Assert.Equal(JsonValueKind.Null, none.GetProperty("color").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Changing_Nothing_Or_A_Label_That_Is_Not_There_Is_Refused()
    {
        var client = await StartAsync(0);
        await client.SendAsync(HttpMethod.Post, "/api/v1/labels", new { name = "Work" });

        var (empty, _) = await client.SendAsync(HttpMethod.Patch, "/api/v1/labels/Work", new { });
        var (missing, problem) = await client.SendAsync(HttpMethod.Patch, "/api/v1/labels/Nobody", new { color = "work" });
        var (missingDelete, _) = await client.SendAsync(HttpMethod.Delete, "/api/v1/labels/Nobody");

        Assert.Equal(HttpStatusCode.BadRequest, empty);
        Assert.Equal(HttpStatusCode.NotFound, missing);
        Assert.DoesNotContain("Nobody", problem.ToString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, missingDelete);
    }

    [Fact]
    public async Task A_Deleted_Label_Is_Gone_From_The_List_And_From_Every_Message_And_The_Messages_Stay()
    {
        var client = await StartAsync(2);
        var ids = (await client.MessagesAsync()).Select(m => m.Id).ToList();
        await client.SendAsync(HttpMethod.Patch, $"/api/v1/messages/{ids[0]}/labels", new { add = new[] { "Work", "Keep" } });
        await client.SendAsync(HttpMethod.Patch, $"/api/v1/messages/{ids[1]}/labels", new { add = new[] { "Work" } });
        await client.SendAsync(HttpMethod.Post, "/api/v1/labels", new { name = "Work", color = "work" }); // already there: refused, the color stays unset

        var (status, _) = await client.SendAsync(HttpMethod.Delete, "/api/v1/labels/work");
        var messages = await client.MessagesAsync();

        Assert.Equal(HttpStatusCode.NoContent, status);
        Assert.Equal(["Keep"], messages[0].Labels);
        Assert.Empty(messages[1].Labels);
        Assert.Equal(2, messages.Count);
        Assert.DoesNotContain("Work", await client.NamesAsync(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_Label_Put_On_A_Message_By_Another_Program_Is_In_The_List()
    {
        var client = await StartAsync(1);
        await dovecot.DoveadmAsync(Token, "flags", "add", "-u", client.Account.Address, "FromElsewhere", "mailbox", "INBOX", "uid", "1");

        Assert.Contains("FromElsewhere", await client.NamesAsync());
    }

    [Fact]
    public async Task Labels_Need_A_Session_And_Changes_Need_The_Anti_Forgery_Token()
    {
        var client = await StartAsync(0);

        using var anonymous = new HttpRequestMessage(HttpMethod.Get, "/api/v1/labels");
        var noSession = await client.Http.SendAsync(anonymous, Token);
        using var withoutToken = new HttpRequestMessage(HttpMethod.Post, "/api/v1/labels") { Content = JsonContent.Create(new { name = "Work" }) };
        withoutToken.Headers.Add("Cookie", $"{SessionCookie.Name}={client.Session}");
        var noToken = await client.Http.SendAsync(withoutToken, Token);

        Assert.Equal(HttpStatusCode.Unauthorized, noSession.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, noToken.StatusCode);
        Assert.Empty(await client.NamesAsync());
    }

    [Fact]
    public async Task The_Labels_Of_Two_Users_Are_Kept_Apart()
    {
        var alice = await StartAsync(0);
        var bob = await StartAsync(0);
        await alice.SendAsync(HttpMethod.Post, "/api/v1/labels", new { name = "Mine", color = "work" });

        Assert.Empty(await bob.NamesAsync());
    }
}
