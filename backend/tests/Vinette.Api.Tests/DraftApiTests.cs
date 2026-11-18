using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vinette.Api.Auth;
using Vinette.Core.Attachments;
using Vinette.Core.Authentication;
using Vinette.Core.Mail;

namespace Vinette.Api.Tests;

/// <summary>The draft endpoints through the real application (VIN-094), with a stand-in for the drafts folder: what the request becomes, and what comes back.</summary>
public sealed class DraftApiTests : IDisposable
{
    private const string Password = "pw";
    private static readonly MessageId SavedId = new("Drafts", 3, 12);
    private static readonly string[] ThreadSoFar = ["first@example.org"];
    private static readonly string[] UnknownFile = ["AAAAAAAAAAAAAAAAAAAAAA"];

    private sealed class AnyoneAuthenticator : IAuthenticator
    {
        public Task<AuthenticationResult> AuthenticateAsync(LoginRequest request, ClientInfo client, CancellationToken cancellationToken) =>
            Task.FromResult(request.Password == Password ? AuthenticationResult.Success : AuthenticationResult.InvalidCredentials);
    }

    private sealed class FakeDrafts : IDrafts
    {
        public List<(OutgoingMessage Message, MessageId? Previous)> Saved { get; } = [];

        public bool NoFolder { get; set; }

        public Exception? Fail { get; set; }

        public OutgoingMessage? Opened { get; set; }

        public List<MessageId> Deleted { get; } = [];

        public Task<SavedDraft?> SaveAsync(MailCredentials credentials, OutgoingMessage message, MessageId? previous, CancellationToken cancellationToken)
        {
            if (Fail is not null)
            {
                throw Fail;
            }

            Saved.Add((message, previous));
            return Task.FromResult(NoFolder ? null : new SavedDraft(SavedId, "made@vinette.test", new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero), true));
        }

        public Task<OpenedDraft?> OpenAsync(MailCredentials credentials, MessageId id, CancellationToken cancellationToken) =>
            Task.FromResult(NoFolder || Opened is null ? null : new OpenedDraft(id, Opened));

        public Task<bool> DeleteAsync(MailCredentials credentials, MessageId id, CancellationToken cancellationToken)
        {
            Deleted.Add(id);
            return Task.FromResult(!NoFolder);
        }
    }

    private readonly string directory = Path.Combine(Path.GetTempPath(), "vinette-draft-api-" + Guid.NewGuid().ToString("N"));
    private readonly FakeDrafts drafts = new();
    private readonly WebApplicationFactory<Program> factory;

    public DraftApiTests() =>
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Attachments:Path", directory);
            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuthenticator>();
                services.AddSingleton<IAuthenticator>(new AnyoneAuthenticator());
                services.RemoveAll<IDrafts>();
                services.AddSingleton<IDrafts>(drafts);
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

    private static async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(User user, HttpMethod method, string url, HttpContent? content = null)
    {
        using var response = await CsrfClient.SendAsync(user.Http, method, url, Token, content, user.Session);
        var text = await response.Content.ReadAsStringAsync(Token);
        return (response.StatusCode, text.Length == 0 ? default : JsonElement.Parse(text));
    }

    private static async Task<string> UploadAsync(User user, string name, byte[] bytes)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        var (status, body) = await SendAsync(user, HttpMethod.Post, "/api/v1/attachments", new MultipartFormDataContent { { file, "file", name } });
        Assert.Equal(HttpStatusCode.Created, status);
        return body.GetProperty("id").GetString()!;
    }

    private static Task<(HttpStatusCode Status, JsonElement Body)> SaveAsync(User user, object request) =>
        SendAsync(user, HttpMethod.Post, "/api/v1/drafts", JsonContent.Create(request));

    [Fact]
    public async Task A_Draft_Needs_A_Session()
    {
        using var http = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        using var response = await http.GetAsync($"/api/v1/drafts/{SavedId}", Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task What_Was_Written_Is_Saved_As_Written_With_The_Bytes_Of_The_Uploaded_Files()
    {
        var user = await SignInAsync();
        var id = await UploadAsync(user, "смета.pdf", [1, 2, 3, 4]);
        var previous = new MessageId("Drafts", 3, 11);

        var (status, body) = await SaveAsync(user, new
        {
            previousId = previous.ToString(),
            messageId = "keep@vinette.test",
            fromName = "Алиса",
            to = new[] { new { name = "Анна", address = "anna@example.org" } },
            bcc = new[] { new { name = "", address = "hidden@example.org" } },
            subject = "Тема",
            text = "Текст",
            attachmentIds = new[] { id, id },
            inReplyTo = "orig@example.org",
            references = ThreadSoFar,
        });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(SavedId.ToString(), body.GetProperty("id").GetString());
        Assert.Equal("made@vinette.test", body.GetProperty("messageId").GetString());
        Assert.True(body.GetProperty("previousRemoved").GetBoolean());
        var (message, replaced) = Assert.Single(drafts.Saved);
        Assert.Equal(previous, replaced);
        Assert.Equal("alice@vinette.test", message.From.Address); // the sender is the signed-in mailbox, whatever the request says
        Assert.Equal("Алиса", message.From.Name);
        Assert.Equal(["anna@example.org"], message.To.Select(a => a.Address));
        Assert.Equal(["hidden@example.org"], message.Bcc.Select(a => a.Address));
        Assert.Equal("Тема", message.Subject);
        Assert.Equal("keep@vinette.test", message.MessageId);
        Assert.Equal("orig@example.org", message.InReplyTo);
        var file = Assert.Single(message.Attachments); // named twice, attached once
        Assert.Equal("смета.pdf", file.FileName);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, file.Content.ToArray());
    }

    [Fact]
    public async Task An_Empty_Draft_Is_Saved_Too()
    {
        var user = await SignInAsync();

        var (status, _) = await SaveAsync(user, new { });

        Assert.Equal(HttpStatusCode.OK, status);
        var (message, replaced) = Assert.Single(drafts.Saved);
        Assert.Null(replaced);
        Assert.Empty(message.To);
        Assert.Equal(string.Empty, message.Subject);
    }

    [Fact]
    public async Task A_Malformed_Id_Or_An_Unknown_File_Is_A_Bad_Request_And_Nothing_Is_Saved()
    {
        var user = await SignInAsync();

        var (badId, badIdBody) = await SaveAsync(user, new { previousId = "not-an-id" });
        var (noFile, _) = await SaveAsync(user, new { attachmentIds = UnknownFile });
        var (otherUsers, _) = await SaveAsync(user, new { attachmentIds = new[] { await UploadAsync(await SignInAsync("bob@vinette.test"), "b.pdf", [9]) } });

        Assert.Equal(HttpStatusCode.BadRequest, badId);
        Assert.Equal("bad_request", badIdBody.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, noFile);
        Assert.Equal(HttpStatusCode.BadRequest, otherUsers); // somebody else's file is no file
        Assert.Empty(drafts.Saved);
    }

    [Fact]
    public async Task What_The_Composer_Refuses_Is_A_Bad_Request()
    {
        var user = await SignInAsync();
        drafts.Fail = new ArgumentException("An address is not an address.");

        var (status, body) = await SaveAsync(user, new { to = new[] { new { name = "", address = "nope" } } });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("bad_request", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_Version_That_Is_Not_In_The_Drafts_Folder_Is_Not_Found()
    {
        var user = await SignInAsync();
        drafts.Fail = new MailMessageNotFoundException("not a draft");

        var (status, body) = await SaveAsync(user, new { previousId = new MessageId("INBOX", 1, 5).ToString() });

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("not_found", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_Mailbox_Without_A_Drafts_Folder_Gets_An_Explanation_Not_A_Failure()
    {
        var user = await SignInAsync();
        drafts.NoFolder = true;

        var (status, body) = await SaveAsync(user, new { subject = "x" });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("no_target_folder", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_Opened_Draft_Puts_Its_Files_Among_The_Uploaded_Ones_And_Comes_Back_Clean()
    {
        var user = await SignInAsync();
        drafts.Opened = new OutgoingMessage(
            new MailAddressInfo("Алиса", "alice@vinette.test"),
            [new MailAddressInfo("Анна", "anna@example.org")],
            [],
            [new MailAddressInfo("", "hidden@example.org")],
            "Тема",
            "Текст",
            "<p>Привет</p><script>alert(1)</script>",
            [new OutgoingAttachment("смета.pdf", "application/pdf", new byte[] { 7, 8, 9 })],
            "orig@example.org",
            ["first@example.org"],
            "made@vinette.test");

        var (status, body) = await SendAsync(user, HttpMethod.Get, $"/api/v1/drafts/{SavedId}");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(SavedId.ToString(), body.GetProperty("id").GetString());
        Assert.Equal("made@vinette.test", body.GetProperty("messageId").GetString());
        Assert.Equal("hidden@example.org", body.GetProperty("bcc")[0].GetProperty("address").GetString());
        var html = body.GetProperty("html").GetString()!;
        Assert.Contains("Привет", html, StringComparison.Ordinal);
        Assert.DoesNotContain("script", html, StringComparison.OrdinalIgnoreCase);
        var file = body.GetProperty("attachments")[0];
        Assert.Equal("смета.pdf", file.GetProperty("fileName").GetString());
        Assert.Equal(3, file.GetProperty("size").GetInt64());

        var store = factory.Services.GetRequiredService<IAttachmentStore>();
        await using var stored = await store.OpenAsync(user.Address, file.GetProperty("id").GetString()!, Token);
        Assert.NotNull(stored); // it can now be named in the next save, and sent
        using var read = new MemoryStream();
        await stored.Content.CopyToAsync(read, Token);
        Assert.Equal(new byte[] { 7, 8, 9 }, read.ToArray());
    }

    [Fact]
    public async Task A_Draft_Whose_Files_Do_Not_Fit_Is_Refused_And_Leaves_No_Files_Behind()
    {
        var user = await SignInAsync();
        var limits = factory.Services.GetRequiredService<IAttachmentStore>().Limits;
        drafts.Opened = new OutgoingMessage(
            new MailAddressInfo("", "alice@vinette.test"),
            [],
            [],
            [],
            "",
            "",
            null,
            [.. Enumerable.Range(0, limits.MaxFiles + 1).Select(i => new OutgoingAttachment($"f{i}.txt", "text/plain", Encoding.ASCII.GetBytes("x")))]);

        var (status, body) = await SendAsync(user, HttpMethod.Get, $"/api/v1/drafts/{SavedId}");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("too_many_attachments", body.GetProperty("code").GetString());
        var left = await factory.Services.GetRequiredService<IAttachmentStore>().ListAsync(user.Address, Token);
        Assert.Empty(left);
    }

    [Fact]
    public async Task A_Draft_That_Is_Gone_Is_Not_Found_And_A_Deleted_One_Goes()
    {
        var user = await SignInAsync();

        var (missing, body) = await SendAsync(user, HttpMethod.Get, $"/api/v1/drafts/{SavedId}");
        var (deleted, _) = await SendAsync(user, HttpMethod.Delete, $"/api/v1/drafts/{SavedId}");
        drafts.NoFolder = true;
        var (noFolder, _) = await SendAsync(user, HttpMethod.Delete, $"/api/v1/drafts/{SavedId}");

        Assert.Equal(HttpStatusCode.NotFound, missing);
        Assert.Equal("not_found", body.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NoContent, deleted);
        Assert.Equal(HttpStatusCode.NotFound, noFolder);
        Assert.Equal([SavedId, SavedId], drafts.Deleted);
    }
}
