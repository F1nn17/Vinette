using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Vinette.Api.Auth;
using Vinette.Api.Mail.Images;
using Vinette.TestSupport;

namespace Vinette.Api.Tests;

/// <summary>
/// "Always show pictures from this sender": the choice is kept in the settings, and it only takes effect when the checks of
/// our mail server confirm the sender (the From line alone is easy to forge). The fetcher is replaced by one that answers
/// from memory.
/// </summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class TrustedImageSendersApiTests(DovecotFixture dovecot) : IDisposable
{
    private const string Banner = "https://cdn.example.org/banner.png";
    private const string Picture = "data:image/png;base64,iVBORw0KGgo=";

    private sealed class FakeFetcher : IExternalImageFetcher
    {
        public int Calls;

        public Task<IReadOnlyDictionary<string, string>> FetchAsync(IReadOnlyList<string> urls, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { [Banner] = Picture });
        }
    }

    private readonly FakeFetcher fetcher = new();
    private readonly string settingsDirectory = Path.Combine(Path.GetTempPath(), "vinette-trust-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        owned.ForEach(f => f.Dispose());
        if (Directory.Exists(settingsDirectory))
        {
            Directory.Delete(settingsDirectory, recursive: true);
        }
    }

    private const string From = "anna@example.org";

    private static string Mail(bool verified) =>
        "From: Anna <" + From + ">\nTo: a@vinette.test\nSubject: Pictures\nMIME-Version: 1.0\n"
        + (verified ? "Authentication-Results: mail.vinette.test; dkim=pass header.d=example.org; spf=pass smtp.mailfrom=example.org; dmarc=pass header.from=example.org\n" : string.Empty)
        + "Content-Type: text/html; charset=utf-8\n\n<p>Hi</p><img src=\"" + Banner + "\">\n";

    private sealed record Session(HttpClient Client, string Cookie, string MessageId);

    private async Task<(Session Session, WebApplicationFactory<Program> Factory)> StartAsync(bool verified)
    {
        var web = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Imap:Host", dovecot.Host);
            b.UseSetting("Imap:Port", dovecot.ImapPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
            b.UseSetting("Imap:Security", "None");
            b.UseSetting("SenderCheck:TrustedAuthservIds:0", "mail.vinette.test");
            b.UseSetting("Settings:Path", settingsDirectory);
            b.ConfigureTestServices(services => services.AddSingleton<IExternalImageFetcher>(fetcher));
        });
        var account = await dovecot.CreateAccountAsync($"trust{Guid.NewGuid():N}"[..14], Token);
        await dovecot.SaveRawMessageAsync(account, "INBOX", Mail(verified), Token);
        var client = web.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = await CsrfClient.PostJsonAsync(client, "/api/v1/auth/login", new { address = account.Address, password = account.Password, rememberMe = false }, Token);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(SessionCookie.Name + "=", StringComparison.Ordinal)).Split(';')[0];
        var list = await GetAsync(client, cookie, "/api/v1/folders/INBOX/messages?preview=false");
        return (new Session(client, cookie, list.GetProperty("items")[0].GetProperty("id").GetString()!), web);
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string cookie, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", cookie);
        var response = await client.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token)).RootElement;
    }

    private static async Task<JsonElement> PatchSettingsAsync(Session s, object body)
    {
        var session = s.Cookie[(SessionCookie.Name.Length + 1)..];
        var response = await CsrfClient.SendAsync(s.Client, HttpMethod.Patch, "/api/v1/settings", Token, JsonContent.Create(body), session);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token)).RootElement;
    }

    private static Task<JsonElement> OpenAsync(Session s) => GetAsync(s.Client, s.Cookie, $"/api/v1/messages/{s.MessageId}");

    private async Task<Session> StartKeepingFactoryAsync(bool verified)
    {
        var (session, web) = await StartAsync(verified);
        owned.Add(web);
        return session;
    }

    private readonly List<WebApplicationFactory<Program>> owned = [];

    [Fact]
    public async Task Nobody_Is_Trusted_By_Default_And_The_Pictures_Wait_For_A_Button()
    {
        var s = await StartKeepingFactoryAsync(verified: true);

        var message = await OpenAsync(s);

        Assert.False(message.GetProperty("imagesTrusted").GetBoolean());
        Assert.True(message.GetProperty("hasExternalImages").GetBoolean());
        Assert.True(message.GetProperty("canShowImages").GetBoolean());
        Assert.Equal(0, fetcher.Calls);
    }

    [Fact]
    public async Task A_Trusted_Sender_Who_Is_Verified_Has_The_Pictures_Shown_Without_Asking()
    {
        var s = await StartKeepingFactoryAsync(verified: true);
        var settings = await PatchSettingsAsync(s, new { trustImagesFrom = From });

        var message = await OpenAsync(s);

        Assert.Equal(From, Assert.Single(settings.GetProperty("trustedImageSenders").EnumerateArray()).GetString());
        Assert.True(message.GetProperty("imagesTrusted").GetBoolean());
        Assert.False(message.GetProperty("canShowImages").GetBoolean());
        Assert.Equal(1, message.GetProperty("imagesShown").GetInt32());
        Assert.Contains(Picture, message.GetProperty("body").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Trusted_Sender_Who_Is_Not_Verified_Still_Waits_For_The_Button()
    {
        // The From line can be forged: without the confirmation of our server, trust is not enough.
        var s = await StartKeepingFactoryAsync(verified: false);
        await PatchSettingsAsync(s, new { trustImagesFrom = From });

        var message = await OpenAsync(s);

        Assert.True(message.GetProperty("imagesTrusted").GetBoolean());
        Assert.True(message.GetProperty("canShowImages").GetBoolean());
        Assert.Equal(0, message.GetProperty("imagesShown").GetInt32());
        Assert.Equal(0, fetcher.Calls);
        Assert.DoesNotContain(Picture, message.GetProperty("body").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_Address_Is_Compared_Without_Regard_To_Case()
    {
        var s = await StartKeepingFactoryAsync(verified: true);
        var settings = await PatchSettingsAsync(s, new { trustImagesFrom = "Anna@EXAMPLE.org" });

        var message = await OpenAsync(s);

        Assert.Equal(From, settings.GetProperty("trustedImageSenders")[0].GetString());
        Assert.Equal(1, message.GetProperty("imagesShown").GetInt32());
    }

    [Fact]
    public async Task Trust_Can_Be_Withdrawn()
    {
        var s = await StartKeepingFactoryAsync(verified: true);
        await PatchSettingsAsync(s, new { trustImagesFrom = From });

        var settings = await PatchSettingsAsync(s, new { untrustImagesFrom = From });
        var message = await OpenAsync(s);

        Assert.Empty(settings.GetProperty("trustedImageSenders").EnumerateArray());
        Assert.False(message.GetProperty("imagesTrusted").GetBoolean());
        Assert.True(message.GetProperty("canShowImages").GetBoolean());
        Assert.Equal(0, fetcher.Calls);
    }
}
