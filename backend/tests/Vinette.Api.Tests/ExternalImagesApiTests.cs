using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Vinette.Api.Auth;
using Vinette.Api.Mail.Images;
using Vinette.TestSupport;

namespace Vinette.Api.Tests;

/// <summary>
/// Pictures from the web through the real application against a real Dovecot. The fetcher is replaced by one that answers
/// from memory, so that the tests need no network; how the real one behaves is in <see cref="ExternalImageFetcherTests"/>.
/// </summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class ExternalImagesApiTests(DovecotFixture dovecot) : IDisposable
{
    private const string Banner = "https://cdn.example.org/banner.png";
    private const string Picture = "data:image/png;base64,iVBORw0KGgo=";

    private sealed class FakeFetcher : IExternalImageFetcher
    {
        public List<string> Asked { get; } = [];

        public Task<IReadOnlyDictionary<string, string>> FetchAsync(IReadOnlyList<string> urls, CancellationToken cancellationToken)
        {
            Asked.AddRange(urls);
            return Task.FromResult<IReadOnlyDictionary<string, string>>(urls.Contains(Banner) ? new Dictionary<string, string> { [Banner] = Picture } : new Dictionary<string, string>());
        }
    }

    private readonly FakeFetcher fetcher = new();
    private readonly List<WebApplicationFactory<Program>> factories = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => factories.ForEach(f => f.Dispose());

    private async Task<JsonElement> OpenAsync(string query, bool enabled = true)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Imap:Host", dovecot.Host);
            b.UseSetting("Imap:Port", dovecot.ImapPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
            b.UseSetting("Imap:Security", "None");
            b.UseSetting("ExternalImages:Enabled", enabled ? "true" : "false");
            b.ConfigureTestServices(services => services.AddSingleton<IExternalImageFetcher>(fetcher));
        });
        factories.Add(factory);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = await CsrfClient.PostJsonAsync(client, "/api/v1/auth/login", new { address = DovecotFixture.Bob.Address, password = DovecotFixture.Bob.Password, rememberMe = false }, Token);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(SessionCookie.Name + "=", StringComparison.Ordinal)).Split(';')[0];

        var list = await Get(client, cookie, "/api/v1/folders/INBOX/messages?limit=100&preview=false");
        var id = list.GetProperty("items").EnumerateArray().First(i => i.GetProperty("subject").GetString() == "Weekly newsletter").GetProperty("id").GetString();
        return await Get(client, cookie, $"/api/v1/messages/{id}{query}");
    }

    private static async Task<JsonElement> Get(HttpClient client, string cookie, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", cookie);
        var response = await client.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonElement.Parse(await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task By_Default_Nothing_Is_Fetched_And_The_Reader_Is_Told_That_Pictures_Can_Be_Shown()
    {
        var message = await OpenAsync(string.Empty);

        Assert.True(message.GetProperty("canShowImages").GetBoolean());
        Assert.Equal(0, message.GetProperty("imagesShown").GetInt32());
        Assert.Empty(fetcher.Asked);
        Assert.DoesNotContain("example.org/banner", message.GetProperty("body").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Asking_For_Pictures_Embeds_What_Was_Fetched_And_Keeps_The_Rest_Out()
    {
        var message = await OpenAsync("?images=true");

        var body = message.GetProperty("body").GetString()!;
        Assert.Contains(Picture, body, StringComparison.Ordinal);
        Assert.Equal(1, message.GetProperty("imagesShown").GetInt32());
        Assert.False(message.GetProperty("canShowImages").GetBoolean());
        Assert.DoesNotContain("tracker.example", body, StringComparison.Ordinal); // never fetched, never kept
        Assert.DoesNotContain("http://", body.Replace("http://www.w3.org", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains(Banner, fetcher.Asked);
        Assert.Equal(fetcher.Asked.Count, fetcher.Asked.Distinct().Count());
    }

    [Fact]
    public async Task When_The_Feature_Is_Off_Pictures_Are_Neither_Offered_Nor_Fetched()
    {
        var message = await OpenAsync("?images=true", enabled: false);

        Assert.False(message.GetProperty("canShowImages").GetBoolean());
        Assert.Equal(0, message.GetProperty("imagesShown").GetInt32());
        Assert.Empty(fetcher.Asked);
        Assert.DoesNotContain(Picture, message.GetProperty("body").GetString(), StringComparison.Ordinal);
    }
}
