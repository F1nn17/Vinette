using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Vinette.Api.Auth;
using Vinette.Core.Mail;
using Vinette.TestSupport;

namespace Vinette.Api.Tests;

/// <summary>
/// Very large mails through the real application against a real Dovecot: they must neither hang the server nor take it
/// down, and a very long body comes cut with a way to ask for more.
/// </summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class HeavyMessageApiTests(DovecotFixture dovecot) : IDisposable
{
    private readonly WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseSetting("Imap:Host", dovecot.Host);
        b.UseSetting("Imap:Port", dovecot.ImapPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        b.UseSetting("Imap:Security", "None");
        b.UseSetting("Imap:MaxViewableMessageBytes", LimitForTests.ToString(System.Globalization.CultureInfo.InvariantCulture));
    });

    /// <summary>A real server takes no mail over 10 MB, so the limit is lowered to make a mail "too large" without a huge one.</summary>
    private const long LimitForTests = 2_000_000;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => factory.Dispose();

    /// <summary>A mailbox of its own holding one generated message; the client is signed in.</summary>
    private async Task<(HttpClient Client, string Cookie, string Id)> StartAsync(long attachmentBytes, int bodyChars, string bodyType)
    {
        var account = await dovecot.CreateAccountAsync($"big{Guid.NewGuid():N}"[..14], Token);
        await dovecot.SaveGeneratedMessageAsync(account, "INBOX", "Generated", attachmentBytes, bodyChars, bodyType, Token);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = await CsrfClient.PostJsonAsync(client, "/api/v1/auth/login", new { address = account.Address, password = account.Password, rememberMe = false }, Token);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(SessionCookie.Name + "=", StringComparison.Ordinal)).Split(';')[0];
        var list = await GetAsync(client, cookie, "/api/v1/folders/INBOX/messages?preview=false");
        return (client, cookie, list.Body.GetProperty("items")[0].GetProperty("id").GetString()!);
    }

    private static async Task<(HttpResponseMessage Response, JsonElement Body)> GetAsync(HttpClient client, string cookie, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", cookie);
        var response = await client.SendAsync(request, Token);
        var text = await response.Content.ReadAsStringAsync(Token);
        return (response, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement);
    }

    [Fact]
    public async Task A_Mail_Over_The_Limit_Is_Refused_Quickly_And_The_Server_Stays_Up()
    {
        var (client, cookie, id) = await StartAsync(attachmentBytes: 3_000_000, bodyChars: 100, bodyType: "plain"); // about 4 MB as base64, over the limit of 2 MB

        var watch = Stopwatch.StartNew();
        var (response, body) = await GetAsync(client, cookie, $"/api/v1/messages/{id}");
        var refusedIn = watch.Elapsed;
        var (health, _) = await GetAsync(client, cookie, "/api/v1/folders/INBOX/messages");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("payload_too_large", body.GetProperty("code").GetString());
        Assert.True(refusedIn < TimeSpan.FromSeconds(10), $"Took {refusedIn}: the mail must be refused without being downloaded");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode); // and the application still answers
    }

    [Fact]
    public async Task A_Mail_Over_The_Limit_Cannot_Be_Downloaded_As_An_Attachment_Either()
    {
        var (client, cookie, id) = await StartAsync(attachmentBytes: 3_000_000, bodyChars: 100, bodyType: "plain");

        var (response, _) = await GetAsync(client, cookie, $"/api/v1/messages/{id}/attachments/1.2");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task A_Mail_Just_Under_The_Limit_Opens_With_Its_Big_Attachment_Listed_And_Downloadable()
    {
        var (client, cookie, id) = await StartAsync(attachmentBytes: 1_300_000, bodyChars: 200, bodyType: "plain"); // about 1.8 MB as base64, just under the limit

        var watch = Stopwatch.StartNew();
        var (response, body) = await GetAsync(client, cookie, $"/api/v1/messages/{id}");
        var openedIn = watch.Elapsed;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(openedIn < TimeSpan.FromSeconds(30), $"Took {openedIn}");
        var attachment = Assert.Single(body.GetProperty("attachments").EnumerateArray());
        Assert.Equal(1_300_000, attachment.GetProperty("size").GetInt64());
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/messages/{id}/attachments/{attachment.GetProperty("partId").GetString()}");
        request.Headers.Add("Cookie", cookie);
        var download = await client.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(1_300_000, (await download.Content.ReadAsByteArrayAsync(Token)).Length);
    }

    [Theory]
    [InlineData("html")]
    [InlineData("plain")]
    public async Task A_Very_Long_Body_Comes_Cut_And_Says_That_More_Can_Be_Asked_For(string bodyType)
    {
        var (client, cookie, id) = await StartAsync(attachmentBytes: 0, bodyChars: 400_000, bodyType: bodyType);

        var (_, cut) = await GetAsync(client, cookie, $"/api/v1/messages/{id}");

        Assert.True(cut.GetProperty("truncated").GetBoolean());
        Assert.True(cut.GetProperty("canShowFull").GetBoolean());
        Assert.NotEqual("empty", cut.GetProperty("bodyFormat").GetString());
        var shown = cut.GetProperty("body").GetString()!.Length;
        Assert.InRange(shown, 1_000, 2 * MessageLimits.DefaultBodyChars + 100_000); // the beginning, not all
    }

    [Theory]
    [InlineData("html")]
    [InlineData("plain")]
    public async Task Asking_For_The_Whole_Body_Gives_More_Of_It_And_Nothing_Is_Cut_If_It_Fits(string bodyType)
    {
        var (client, cookie, id) = await StartAsync(attachmentBytes: 0, bodyChars: 400_000, bodyType: bodyType);
        var (_, cut) = await GetAsync(client, cookie, $"/api/v1/messages/{id}");

        var (_, full) = await GetAsync(client, cookie, $"/api/v1/messages/{id}?full=true");

        Assert.False(full.GetProperty("truncated").GetBoolean());
        Assert.False(full.GetProperty("canShowFull").GetBoolean());
        Assert.True(full.GetProperty("body").GetString()!.Length > cut.GetProperty("body").GetString()!.Length);
    }

    [Fact]
    public async Task A_Body_Longer_Than_Anything_That_Is_Shown_Stays_Cut_Even_When_Asked_For_In_Full()
    {
        var (client, cookie, id) = await StartAsync(attachmentBytes: 0, bodyChars: 1_300_000, bodyType: "plain");

        var (_, full) = await GetAsync(client, cookie, $"/api/v1/messages/{id}?full=true");

        Assert.True(full.GetProperty("truncated").GetBoolean());
        Assert.False(full.GetProperty("canShowFull").GetBoolean()); // there is nothing more to ask for
    }

    [Fact]
    public async Task An_Ordinary_Body_Is_Not_Cut_And_Offers_Nothing_More()
    {
        var (client, cookie, id) = await StartAsync(attachmentBytes: 0, bodyChars: 2_000, bodyType: "html");

        var (_, message) = await GetAsync(client, cookie, $"/api/v1/messages/{id}");

        Assert.False(message.GetProperty("truncated").GetBoolean());
        Assert.False(message.GetProperty("canShowFull").GetBoolean());
    }

    [Theory]
    [InlineData("full=maybe")]
    [InlineData("full=2")]
    public async Task A_Wrong_Value_For_Full_Is_A_400(string query)
    {
        var (client, cookie, id) = await StartAsync(attachmentBytes: 0, bodyChars: 100, bodyType: "plain");

        var (response, _) = await GetAsync(client, cookie, $"/api/v1/messages/{id}?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
