using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vinette.Api.Auth;
using Vinette.Core.Attachments;
using Vinette.Core.Authentication;

namespace Vinette.Api.Tests;

/// <summary>Uploading files to attach to a message through the real application (VIN-093). No mail server needed: the sign-in is scripted.</summary>
public sealed class AttachmentApiTests : IDisposable
{
    private const string Password = "pw";

    private sealed class AnyoneAuthenticator : IAuthenticator
    {
        public Task<AuthenticationResult> AuthenticateAsync(LoginRequest request, ClientInfo client, CancellationToken cancellationToken) =>
            Task.FromResult(request.Password == Password ? AuthenticationResult.Success : AuthenticationResult.InvalidCredentials);
    }

    private readonly string directory = Path.Combine(Path.GetTempPath(), "vinette-attachment-api-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> factory;

    public AttachmentApiTests() =>
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Attachments:Path", directory);
            b.UseSetting("Attachments:MaxFileBytes", "1000");
            b.UseSetting("Attachments:MaxTotalBytes", "2500");
            b.UseSetting("Attachments:MaxFiles", "3");
            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuthenticator>();
                services.AddSingleton<IAuthenticator>(new AnyoneAuthenticator());
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

    private sealed record User(HttpClient Http, string Address, string Session);

    private async Task<User> SignInAsync(string address = "alice@vinette.test")
    {
        var http = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = await CsrfClient.PostJsonAsync(http, "/api/v1/auth/login", new { address, password = Password, rememberMe = false }, Token);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(SessionCookie.Name + "=", StringComparison.Ordinal));
        return new User(http, address, cookie[(SessionCookie.Name.Length + 1)..].Split(';')[0]);
    }

    private static MultipartFormDataContent Form(string fileName, byte[] content, string contentType = "application/pdf")
    {
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return new MultipartFormDataContent { { file, "file", fileName } };
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> UploadAsync(User user, HttpContent form)
    {
        using var response = await CsrfClient.SendAsync(user.Http, HttpMethod.Post, "/api/v1/attachments", Token, form, user.Session);
        var text = await response.Content.ReadAsStringAsync(Token);
        return (response.StatusCode, text.Length == 0 ? default : JsonElement.Parse(text));
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(User user, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={user.Session}");
        using var response = await user.Http.SendAsync(request, Token);
        var text = await response.Content.ReadAsStringAsync(Token);
        return (response.StatusCode, text.Length == 0 ? default : JsonElement.Parse(text));
    }

    private static async Task<HttpStatusCode> DeleteAsync(User user, string id)
    {
        using var response = await CsrfClient.SendAsync(user.Http, HttpMethod.Delete, $"/api/v1/attachments/{id}", Token, null, user.Session);
        return response.StatusCode;
    }

    [Fact]
    public async Task A_File_Is_Uploaded_And_Kept_With_Its_Name_Type_And_Bytes()
    {
        var alice = await SignInAsync();
        var bytes = Enumerable.Range(0, 800).Select(i => (byte)(i % 251)).ToArray();

        var (status, body) = await UploadAsync(alice, Form("Договор №5.pdf", bytes));

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Matches("^[A-Za-z0-9_-]{22}$", body.GetProperty("id").GetString());
        Assert.Equal("Договор №5.pdf", body.GetProperty("fileName").GetString());
        Assert.Equal("application/pdf", body.GetProperty("contentType").GetString());
        Assert.Equal(800, body.GetProperty("size").GetInt64());
        var store = factory.Services.GetRequiredService<IAttachmentStore>();
        await using var kept = await store.OpenAsync(alice.Address, body.GetProperty("id").GetString()!, Token) ?? throw new InvalidOperationException();
        using var read = new MemoryStream();
        await kept.Content.CopyToAsync(read, Token);
        Assert.Equal(bytes, read.ToArray());
    }

    [Fact]
    public async Task A_Type_That_Is_Not_On_The_Short_List_Is_Sent_As_Plain_Bytes_And_A_Path_In_The_Name_Is_Dropped()
    {
        var alice = await SignInAsync();

        var (_, body) = await UploadAsync(alice, Form("../../page.html", [1, 2, 3], "text/html"));

        Assert.Equal("page.html", body.GetProperty("fileName").GetString());
        Assert.Equal("application/octet-stream", body.GetProperty("contentType").GetString());
    }

    [Fact]
    public async Task What_Waits_Is_Listed_With_The_Limits_So_That_The_Screen_Can_Refuse_A_File_In_Advance()
    {
        var alice = await SignInAsync();
        _ = await UploadAsync(alice, Form("a.pdf", new byte[100]));
        _ = await UploadAsync(alice, Form("b.pdf", new byte[200]));

        var (status, body) = await GetAsync(alice, "/api/v1/attachments");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["a.pdf", "b.pdf"], body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("fileName").GetString()));
        var limits = body.GetProperty("limits");
        Assert.Equal((1000, 2500, 3), (limits.GetProperty("maxFileBytes").GetInt64(), limits.GetProperty("maxTotalBytes").GetInt64(), limits.GetProperty("maxFiles").GetInt32()));
    }

    [Fact]
    public async Task A_File_Is_Taken_Off_And_Taking_It_Off_Again_Finds_Nothing()
    {
        var alice = await SignInAsync();
        var id = (await UploadAsync(alice, Form("a.pdf", new byte[100]))).Body.GetProperty("id").GetString()!;

        Assert.Equal(HttpStatusCode.NoContent, await DeleteAsync(alice, id));
        Assert.Equal(HttpStatusCode.NotFound, await DeleteAsync(alice, id));

        Assert.Empty((await GetAsync(alice, "/api/v1/attachments")).Body.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Nobody_Can_See_Or_Take_Off_Another_Mailboxs_File()
    {
        var alice = await SignInAsync("alice@vinette.test");
        var bob = await SignInAsync("bob@vinette.test");
        var id = (await UploadAsync(alice, Form("secret.pdf", new byte[100]))).Body.GetProperty("id").GetString()!;

        Assert.Empty((await GetAsync(bob, "/api/v1/attachments")).Body.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, await DeleteAsync(bob, id));
        Assert.Single((await GetAsync(alice, "/api/v1/attachments")).Body.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task A_File_Over_The_Limit_Is_Refused_With_A_Clear_Code_And_Leaves_Nothing()
    {
        var alice = await SignInAsync();

        var (status, problem) = await UploadAsync(alice, Form("big.bin", new byte[1001]));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status);
        Assert.Equal("attachment_too_large", problem.GetProperty("code").GetString());
        Assert.Empty((await GetAsync(alice, "/api/v1/attachments")).Body.GetProperty("items").EnumerateArray());
        Assert.Empty(Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories) : []);
    }

    [Fact]
    public async Task Files_That_Together_Are_Too_Big_Are_Refused_With_Their_Own_Code()
    {
        var alice = await SignInAsync();
        _ = await UploadAsync(alice, Form("1.bin", new byte[1000]));
        _ = await UploadAsync(alice, Form("2.bin", new byte[1000]));

        var (status, problem) = await UploadAsync(alice, Form("3.bin", new byte[600]));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status);
        Assert.Equal("attachments_too_large", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Too_Many_Files_Are_Refused_With_Their_Own_Code()
    {
        var alice = await SignInAsync();
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await UploadAsync(alice, Form($"{i}.bin", new byte[10]))).Status);
        }

        var (status, problem) = await UploadAsync(alice, Form("4.bin", new byte[10]));

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("too_many_attachments", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_Form_Without_A_File_Is_A_Bad_Request()
    {
        var alice = await SignInAsync();
        var form = new MultipartFormDataContent { { new StringContent("just text"), "comment" } };

        var (status, problem) = await UploadAsync(alice, form);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("bad_request", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Something_That_Is_Not_A_Form_Is_A_Bad_Request()
    {
        var alice = await SignInAsync();

        var (status, _) = await UploadAsync(alice, new ByteArrayContent(new byte[100]) { Headers = { ContentType = new MediaTypeHeaderValue("application/pdf") } });

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Without_A_Session_Nothing_Can_Be_Uploaded_Listed_Or_Removed()
    {
        var http = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        using var post = new HttpRequestMessage(HttpMethod.Post, "/api/v1/attachments") { Content = Form("a.pdf", new byte[10]) };
        using var get = new HttpRequestMessage(HttpMethod.Get, "/api/v1/attachments");
        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/attachments/aaaaaaaaaaaaaaaaaaaaaa");

        foreach (var request in new[] { post, get, delete })
        {
            using var response = await http.SendAsync(request, Token);
            Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"{request.Method} answered {response.StatusCode}");
        }
    }

    [Fact]
    public async Task An_Upload_And_A_Removal_Need_The_Anti_Forgery_Token()
    {
        var alice = await SignInAsync();

        using var upload = new HttpRequestMessage(HttpMethod.Post, "/api/v1/attachments") { Content = Form("a.pdf", new byte[10]) };
        upload.Headers.Add("Cookie", $"{SessionCookie.Name}={alice.Session}");
        using var remove = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/attachments/aaaaaaaaaaaaaaaaaaaaaa");
        remove.Headers.Add("Cookie", $"{SessionCookie.Name}={alice.Session}");

        using var uploaded = await alice.Http.SendAsync(upload, Token);
        using var removed = await alice.Http.SendAsync(remove, Token);

        Assert.Equal(HttpStatusCode.Forbidden, uploaded.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, removed.StatusCode);
        Assert.Empty((await GetAsync(alice, "/api/v1/attachments")).Body.GetProperty("items").EnumerateArray());
    }
}
