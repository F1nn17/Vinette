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

/// <summary>The signatures through the settings endpoints (VIN-104): saved with ids and one default, kept per user, refused outside the limits.</summary>
public sealed class SignaturesApiTests : IDisposable
{
    private const string Password = "pw";

    private sealed class AnyoneAuthenticator : IAuthenticator
    {
        public Task<AuthenticationResult> AuthenticateAsync(LoginRequest request, ClientInfo client, CancellationToken cancellationToken) =>
            Task.FromResult(AuthenticationResult.Success);
    }

    private readonly string directory = Path.Combine(Path.GetTempPath(), "vinette-signatures-api-" + Guid.NewGuid().ToString("N"));

    private readonly WebApplicationFactory<Program> factory;

    public SignaturesApiTests() =>
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Settings:Path", directory);
            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuthenticator>();
                services.AddSingleton<IAuthenticator, AnyoneAuthenticator>();
            });
        });

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
    public async Task A_New_User_Has_No_Signatures()
    {
        var (client, session) = await SignInAsync("alice@vinette.test");

        var (_, body) = await GetAsync(client, session);

        Assert.Equal(0, body.GetProperty("signatures").GetArrayLength());
    }

    [Fact]
    public async Task Signatures_Are_Saved_With_New_Ids_And_One_Default_And_Come_Back_To_The_Next_Sign_In()
    {
        var (client, session) = await SignInAsync("alice@vinette.test");
        var windows = "Regards," + (char)13 + (char)10 + "Alice" + (char)13 + (char)10 + (char)13 + (char)10;

        var (status, saved) = await PatchAsync(client, session, new
        {
            signatures = new object[]
            {
                new { name = " Work ", text = windows, @default = true },
                new { name = "Home", text = "Cheers", @default = true },
            },
        });
        var (again, againSession) = await SignInAsync("alice@vinette.test");
        var (_, next) = await GetAsync(again, againSession);

        Assert.Equal(HttpStatusCode.OK, status);
        var list = saved.GetProperty("signatures").EnumerateArray().ToList();
        Assert.Equal(2, list.Count);
        Assert.NotEqual(list[0].GetProperty("id").GetString(), list[1].GetProperty("id").GetString());
        Assert.Equal("Work", list[0].GetProperty("name").GetString());
        Assert.Equal("Regards," + (char)10 + "Alice", list[0].GetProperty("text").GetString()); // line ends normalized, the tail trimmed
        Assert.True(list[0].GetProperty("default").GetBoolean());
        Assert.False(list[1].GetProperty("default").GetBoolean()); // only the first that asks counts
        Assert.Equal(saved.GetProperty("signatures").ToString(), next.GetProperty("signatures").ToString());
    }

    [Fact]
    public async Task A_Signature_Keeps_Its_Id_When_The_List_Is_Sent_Again_And_What_Is_Left_Out_Is_Gone()
    {
        var (client, session) = await SignInAsync("alice@vinette.test");
        var (_, first) = await PatchAsync(client, session, new { signatures = new object[] { new { name = "A", text = "a" }, new { name = "B", text = "b" } } });
        var ids = first.GetProperty("signatures").EnumerateArray().Select(g => g.GetProperty("id").GetString()).ToList();

        var (_, second) = await PatchAsync(client, session, new
        {
            signatures = new object[] { new { id = ids[1], name = "B2", text = "b2", @default = true }, new { id = "unknown", name = "C", text = "c" } },
        });

        var list = second.GetProperty("signatures").EnumerateArray().ToList();
        Assert.Equal(2, list.Count);
        Assert.Equal(ids[1], list[0].GetProperty("id").GetString());
        Assert.Equal("B2", list[0].GetProperty("name").GetString());
        Assert.NotEqual("unknown", list[1].GetProperty("id").GetString()); // an id the user does not have is never taken
        Assert.DoesNotContain(list, g => g.GetProperty("id").GetString() == ids[0]);
    }

    [Fact]
    public async Task A_Patch_Without_Signatures_Leaves_Them_And_An_Empty_List_Removes_Them()
    {
        var (client, session) = await SignInAsync("alice@vinette.test");
        _ = await PatchAsync(client, session, new { signatures = new object[] { new { name = "A", text = "a", @default = true } } });

        var (_, untouched) = await PatchAsync(client, session, new { density = "compact" });
        var (_, removed) = await PatchAsync(client, session, new { signatures = Array.Empty<object>() });

        Assert.Equal(1, untouched.GetProperty("signatures").GetArrayLength());
        Assert.Equal(0, removed.GetProperty("signatures").GetArrayLength());
    }

    [Fact]
    public async Task A_Signature_Outside_The_Limits_Is_A_Bad_Request_And_Nothing_Is_Saved()
    {
        var (client, session) = await SignInAsync("alice@vinette.test");

        var (status, body) = await PatchAsync(client, session, new { signatures = new object[] { new { name = "", text = "a" } } });
        var (_, after) = await GetAsync(client, session);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("bad_request", body.GetProperty("code").GetString());
        Assert.Equal(0, after.GetProperty("signatures").GetArrayLength());
    }

    [Fact]
    public async Task Two_Users_Do_Not_See_Each_Others_Signatures()
    {
        var (alice, aliceSession) = await SignInAsync("alice@vinette.test");
        var (bob, bobSession) = await SignInAsync("bob@vinette.test");
        _ = await PatchAsync(alice, aliceSession, new { signatures = new object[] { new { name = "Mine", text = "secret" } } });

        var (_, theirs) = await GetAsync(bob, bobSession);

        Assert.Equal(0, theirs.GetProperty("signatures").GetArrayLength());
    }
}
