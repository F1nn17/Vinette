using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vinette.Api.Auth;
using Vinette.Core.Authentication;

namespace Vinette.Api.Tests;

/// <summary>The settings endpoints through the real application, with a scripted sign-in and a temporary settings directory.</summary>
public sealed class SettingsApiTests : IDisposable
{
    private const string Password = "pw";

    private sealed class AnyoneAuthenticator : IAuthenticator
    {
        public Task<AuthenticationResult> AuthenticateAsync(LoginRequest request, ClientInfo client, CancellationToken cancellationToken) =>
            Task.FromResult(AuthenticationResult.Success);
    }

    private readonly string directory = Path.Combine(Path.GetTempPath(), "vinette-settings-api-" + Guid.NewGuid().ToString("N"));

    private readonly WebApplicationFactory<Program> factory;

    public SettingsApiTests()
    {
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Settings:Path", directory);
            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuthenticator>();
                services.AddSingleton<IAuthenticator, AnyoneAuthenticator>();
            });
        });
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        factory.Dispose();
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private async Task<(HttpClient Client, string Session)> SignInAsync(string address)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = await CsrfClient.PostJsonAsync(client, "/api/v1/auth/login", new { address, password = Password, rememberMe = false }, Token);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(SessionCookie.Name + "=", StringComparison.Ordinal));
        return (client, cookie[(SessionCookie.Name.Length + 1)..].Split(';')[0]);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(HttpClient client, string session)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/settings");
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={session}");
        var response = await client.SendAsync(request, Token);
        return (response.StatusCode, JsonElement.Parse(await response.Content.ReadAsStringAsync(Token)));
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PatchAsync(HttpClient client, string session, object body)
    {
        var response = await CsrfClient.SendAsync(client, HttpMethod.Patch, "/api/v1/settings", Token, JsonContent.Create(body), session);
        return (response.StatusCode, JsonElement.Parse(await response.Content.ReadAsStringAsync(Token)));
    }

    [Fact]
    public async Task A_New_User_Gets_The_Defaults()
    {
        var (client, session) = await SignInAsync("alice@vinette.test");

        var (status, body) = await GetAsync(client, session);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("comfortable", body.GetProperty("density").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("language").ValueKind);
        Assert.Empty(body.GetProperty("labelColors").EnumerateObject());
    }

    [Fact]
    public async Task A_Change_Is_Saved_Returned_And_Seen_By_The_Next_Sign_In()
    {
        var (client, session) = await SignInAsync("alice@vinette.test");

        var (status, saved) = await PatchAsync(client, session, new { density = "compact" });
        var (_, again) = await SignInAsync("alice@vinette.test").ContinueWith(t => GetAsync(t.Result.Client, t.Result.Session), Token).Unwrap();

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("compact", saved.GetProperty("density").GetString());
        Assert.Equal("compact", again.GetProperty("density").GetString());
    }

    [Fact]
    public async Task Only_The_Members_That_Are_Sent_Change()
    {
        var (client, session) = await SignInAsync("alice@vinette.test");
        await PatchAsync(client, session, new { density = "compact", language = "en", labelColors = new Dictionary<string, string> { ["Работа"] = "work" } });

        var (_, body) = await PatchAsync(client, session, new { language = "ru" });

        Assert.Equal("compact", body.GetProperty("density").GetString());
        Assert.Equal("ru", body.GetProperty("language").GetString());
        Assert.Equal("work", body.GetProperty("labelColors").GetProperty("Работа").GetString());
    }

    [Fact]
    public async Task Nobody_Is_Trusted_With_Pictures_At_First()
    {
        var (client, session) = await SignInAsync("alice@vinette.test");

        var (_, body) = await GetAsync(client, session);

        Assert.Empty(body.GetProperty("trustedImageSenders").EnumerateArray());
    }

    [Fact]
    public async Task A_Sender_Can_Be_Trusted_And_Then_Not_Trusted_And_Other_Settings_Stay()
    {
        var (client, session) = await SignInAsync("alice@vinette.test");
        await PatchAsync(client, session, new { density = "compact" });

        var (_, trusted) = await PatchAsync(client, session, new { trustImagesFrom = "Anna@Example.org" });
        var (_, again) = await GetAsync(client, session);
        var (_, revoked) = await PatchAsync(client, session, new { untrustImagesFrom = "anna@example.org" });

        Assert.Equal("anna@example.org", Assert.Single(trusted.GetProperty("trustedImageSenders").EnumerateArray()).GetString());
        Assert.Equal("compact", trusted.GetProperty("density").GetString());
        Assert.Single(again.GetProperty("trustedImageSenders").EnumerateArray());
        Assert.Empty(revoked.GetProperty("trustedImageSenders").EnumerateArray());
    }

    [Fact]
    public async Task The_List_Of_Trusted_Senders_Is_Kept_Apart_Per_User()
    {
        var (alice, aliceSession) = await SignInAsync("alice@vinette.test");
        var (bob, bobSession) = await SignInAsync("bob@vinette.test");
        await PatchAsync(alice, aliceSession, new { trustImagesFrom = "anna@example.org" });

        var (_, body) = await GetAsync(bob, bobSession);

        Assert.Empty(body.GetProperty("trustedImageSenders").EnumerateArray());
    }

    [Theory]
    [InlineData("trustImagesFrom")]
    [InlineData("untrustImagesFrom")]
    public async Task An_Empty_Address_Is_Refused(string member)
    {
        var (client, session) = await SignInAsync("alice@vinette.test");

        var response = await CsrfClient.SendAsync(client, HttpMethod.Patch, "/api/v1/settings", Token, JsonContent.Create(new Dictionary<string, string> { [member] = "  " }), session);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_Empty_Language_Goes_Back_To_Following_The_Browser()
    {
        var (client, session) = await SignInAsync("alice@vinette.test");
        await PatchAsync(client, session, new { language = "en" });

        var (_, body) = await PatchAsync(client, session, new { language = string.Empty });

        Assert.Equal(JsonValueKind.Null, body.GetProperty("language").ValueKind);
    }

    [Fact]
    public async Task Settings_Of_Two_Users_Are_Kept_Apart()
    {
        var (alice, aliceSession) = await SignInAsync("alice@vinette.test");
        var (bob, bobSession) = await SignInAsync("bob@vinette.test");
        await PatchAsync(alice, aliceSession, new { density = "compact" });

        var (_, body) = await GetAsync(bob, bobSession);

        Assert.Equal("comfortable", body.GetProperty("density").GetString());
    }

    [Theory]
    [InlineData("{\"density\":\"tight\"}")]
    [InlineData("{\"density\":\"Compact\"}")]
    [InlineData("{\"language\":\"not a language\"}")]
    [InlineData("{\"labelColors\":{\"\":\"work\"}}")]
    public async Task Wrong_Values_Are_A_400_With_A_Code_And_Nothing_Is_Saved(string json)
    {
        var (client, session) = await SignInAsync("alice@vinette.test");

        var response = await CsrfClient.SendAsync(client, HttpMethod.Patch, "/api/v1/settings", Token, new StringContent(json, System.Text.Encoding.UTF8, "application/json"), session);
        var problem = JsonElement.Parse(await response.Content.ReadAsStringAsync(Token));
        var (_, after) = await GetAsync(client, session);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("bad_request", problem.GetProperty("code").GetString());
        Assert.Equal("comfortable", after.GetProperty("density").GetString());
    }

    [Fact]
    public async Task Without_A_Session_Reading_And_Writing_Are_Refused()
    {
        var client = factory.CreateClient();

        var read = await client.GetAsync("/api/v1/settings", Token);
        var write = await CsrfClient.SendAsync(client, HttpMethod.Patch, "/api/v1/settings", Token, JsonContent.Create(new { density = "compact" }));

        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode);
    }

    [Fact]
    public async Task Writing_Needs_The_Anti_Forgery_Token()
    {
        var (client, session) = await SignInAsync("alice@vinette.test");
        using var request = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/settings") { Content = JsonContent.Create(new { density = "compact" }) };
        request.Headers.Add("Origin", CsrfClient.Origin);
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={session}");

        var response = await client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
