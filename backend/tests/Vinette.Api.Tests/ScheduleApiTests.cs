using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vinette.Api.Auth;
using Vinette.Core.Authentication;
using Vinette.Core.Mail;

namespace Vinette.Api.Tests;

/// <summary>The scheduling endpoints through the real application (VIN-107), with stand-ins for the drafts folder and the mail server: what is accepted, what is refused, and what is tidied.</summary>
public sealed class ScheduleApiTests : IDisposable
{
    private const string Password = "pw";
    private static readonly MessageId DraftId = new("Drafts", 3, 12);

    private sealed class AnyoneAuthenticator : IAuthenticator
    {
        public Task<AuthenticationResult> AuthenticateAsync(LoginRequest request, ClientInfo client, CancellationToken cancellationToken) =>
            Task.FromResult(request.Password == Password ? AuthenticationResult.Success : AuthenticationResult.InvalidCredentials);
    }

    private sealed class FakeDrafts : IDrafts
    {
        public List<MessageId> Deleted { get; } = [];

        public Task<SavedDraft?> SaveAsync(MailCredentials credentials, OutgoingMessage message, MessageId? previous, CancellationToken cancellationToken) =>
            Task.FromResult<SavedDraft?>(null);

        public Task<OpenedDraft?> OpenAsync(MailCredentials credentials, MessageId id, CancellationToken cancellationToken) =>
            Task.FromResult<OpenedDraft?>(null);

        public Task<bool> DeleteAsync(MailCredentials credentials, MessageId id, CancellationToken cancellationToken)
        {
            Deleted.Add(id);
            return Task.FromResult(true);
        }
    }

#pragma warning disable CA1852 // DispatchProxy needs a class that is not sealed.
    private class SessionDouble : DispatchProxy
    {
        public Dictionary<string, Func<object?[], object?>> Members { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Members.TryGetValue(targetMethod!.Name, out var member) ? member(args ?? []) : throw new NotSupportedException(targetMethod.Name);

        public static IMailSession Create(Dictionary<string, Func<object?[], object?>> members)
        {
            var proxy = DispatchProxy.Create<IMailSession, SessionDouble>();
            ((SessionDouble)(object)proxy).Members = members;
            return proxy;
        }
    }
#pragma warning restore CA1852

    private sealed class FakeGateway : IMailGateway
    {
        public List<(string Folder, bool Draft)> Appended { get; } = [];

        public List<MailFolderInfo> Folders { get; } = [new("Drafts", "Drafts", null, FolderRole.Drafts, true, 0, 0, true)];

        public Task<TResult> ExecuteAsync<TResult>(MailCredentials credentials, Func<IMailSession, CancellationToken, Task<TResult>> command, CancellationToken cancellationToken, bool safeToRepeat = true)
        {
            var session = SessionDouble.Create(new Dictionary<string, Func<object?[], object?>>
            {
                ["GetFoldersAsync"] = _ => Task.FromResult<IReadOnlyList<MailFolderInfo>>(Folders),
                ["AppendMessageAsync"] = args =>
                {
                    Appended.Add(((string)args[0]!, (bool)args[3]!));
                    return Task.FromResult<MessageId?>(null);
                },
            });
            return command(session, cancellationToken);
        }

        public Task ReleaseAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private readonly string directory = Path.Combine(Path.GetTempPath(), "vinette-schedule-api-" + Guid.NewGuid().ToString("N"));
    private readonly FakeDrafts drafts = new();
    private readonly FakeGateway gateway = new();
    private readonly WebApplicationFactory<Program> factory;

    public ScheduleApiTests() =>
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Attachments:Path", Path.Combine(directory, "files"));
            b.UseSetting("Scheduling:Path", Path.Combine(directory, "queue"));
            b.UseSetting("Scheduling:Enabled", "false"); // nothing is sent here: only the endpoints are looked at
            b.UseSetting("Scheduling:MaxPerMailbox", "2");
            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuthenticator>();
                services.AddSingleton<IAuthenticator>(new AnyoneAuthenticator());
                services.RemoveAll<IDrafts>();
                services.AddSingleton<IDrafts>(drafts);
                services.RemoveAll<IMailGateway>();
                services.AddSingleton<IMailGateway>(gateway);
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

    private sealed record User(HttpClient Http, string Session);

    private async Task<User> SignInAsync(string address = "alice@vinette.test")
    {
        var http = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = await CsrfClient.PostJsonAsync(http, "/api/v1/auth/login", new { address, password = Password, rememberMe = false }, Token);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(SessionCookie.Name + "=", StringComparison.Ordinal));
        return new User(http, cookie[(SessionCookie.Name.Length + 1)..].Split(';')[0]);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> CallAsync(User user, HttpMethod method, string url, HttpContent? content = null)
    {
        using var response = await CsrfClient.SendAsync(user.Http, method, url, Token, content, user.Session);
        var text = await response.Content.ReadAsStringAsync(Token);
        return (response.StatusCode, text.Length == 0 ? default : JsonElement.Parse(text));
    }

    private static async Task<string> UploadAsync(User user, string name, byte[] bytes)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        var (status, body) = await CallAsync(user, HttpMethod.Post, "/api/v1/attachments", new MultipartFormDataContent { { file, "file", name } });
        Assert.Equal(HttpStatusCode.Created, status);
        return body.GetProperty("id").GetString()!;
    }

    private static Task<(HttpStatusCode Status, JsonElement Body)> ScheduleAsync(User user, object request) =>
        CallAsync(user, HttpMethod.Post, "/api/v1/scheduled", JsonContent.Create(request));

    private static object Simple(DateTimeOffset sendAt, params string[] attachmentIds) => new
    {
        sendAt,
        to = new[] { new { name = "Анна", address = "anna@example.org" } },
        bcc = new[] { new { name = "", address = "hidden@example.org" } },
        subject = "Смета",
        text = "Текст",
        attachmentIds,
    };

    [Fact]
    public async Task Every_Call_Needs_A_Session()
    {
        using var http = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        using var post = await CsrfClient.PostJsonAsync(http, "/api/v1/scheduled", Simple(DateTimeOffset.UtcNow.AddHours(1)), Token);
        using var get = await http.GetAsync("/api/v1/scheduled", Token);
        using var delete = await CsrfClient.SendAsync(http, HttpMethod.Delete, "/api/v1/scheduled/" + new string('0', 32), Token);

        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized], [post.StatusCode, get.StatusCode, delete.StatusCode]);
    }

    [Fact]
    public async Task A_Message_Is_Queued_And_Listed_With_What_The_List_Needs_And_Without_The_Hidden_Recipients()
    {
        var user = await SignInAsync();
        var sendAt = DateTimeOffset.UtcNow.AddHours(2);

        var (status, created) = await ScheduleAsync(user, Simple(sendAt));
        var (_, list) = await CallAsync(user, HttpMethod.Get, "/api/v1/scheduled");

        Assert.Equal(HttpStatusCode.Created, status);
        var item = created.GetProperty("item");
        Assert.Equal("Смета", item.GetProperty("subject").GetString());
        Assert.Equal("pending", item.GetProperty("state").GetString());
        Assert.Equal("anna@example.org", item.GetProperty("to")[0].GetProperty("address").GetString());
        Assert.DoesNotContain("hidden@example.org", created.ToString(), StringComparison.Ordinal);
        Assert.True(created.GetProperty("draftRemoved").GetBoolean());
        var listed = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(item.GetProperty("id").GetString(), listed.GetProperty("id").GetString());
        Assert.DoesNotContain("hidden@example.org", list.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Once_It_Is_Queued_The_Uploaded_Files_And_The_Draft_It_Was_Written_From_Are_Gone()
    {
        var user = await SignInAsync();
        var id = await UploadAsync(user, "a.pdf", [1, 2, 3]);

        var (status, _) = await ScheduleAsync(user, new
        {
            sendAt = DateTimeOffset.UtcNow.AddHours(2),
            to = new[] { new { name = "", address = "anna@example.org" } },
            subject = "s",
            text = "t",
            attachmentIds = new[] { id },
            draftId = DraftId.ToString(),
        });

        Assert.Equal(HttpStatusCode.Created, status);
        var (_, waiting) = await CallAsync(user, HttpMethod.Get, "/api/v1/attachments");
        Assert.Equal(0, waiting.GetProperty("items").GetArrayLength());
        Assert.Equal(DraftId, Assert.Single(drafts.Deleted));
    }

    [Fact]
    public async Task What_Is_Refused_Is_A_Clear_Problem_And_Leaves_The_Files_And_The_Draft_Where_They_Are()
    {
        var user = await SignInAsync();
        var id = await UploadAsync(user, "a.pdf", [1]);
        object At(DateTimeOffset when) => new
        {
            sendAt = when,
            to = new[] { new { name = "", address = "anna@example.org" } },
            subject = "s",
            text = "t",
            attachmentIds = new[] { id },
            draftId = DraftId.ToString(),
        };

        var (tooSoon, soon) = await ScheduleAsync(user, At(DateTimeOffset.UtcNow.AddSeconds(5)));
        var (tooLate, late) = await ScheduleAsync(user, At(DateTimeOffset.UtcNow.AddDays(30)));

        Assert.Equal(HttpStatusCode.BadRequest, tooSoon);
        Assert.Equal("schedule_too_soon", soon.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, tooLate);
        Assert.Equal("schedule_too_late", late.GetProperty("code").GetString());
        var (_, waiting) = await CallAsync(user, HttpMethod.Get, "/api/v1/attachments");
        Assert.Equal(1, waiting.GetProperty("items").GetArrayLength());
        Assert.Empty(drafts.Deleted);
        var (_, list) = await CallAsync(user, HttpMethod.Get, "/api/v1/scheduled");
        Assert.Equal(0, list.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task A_Missing_Time_No_Recipient_An_Unknown_File_Or_A_Draft_That_Is_Not_An_Id_Is_A_Bad_Request()
    {
        var user = await SignInAsync();
        var when = DateTimeOffset.UtcNow.AddHours(1);

        var (noTime, _) = await ScheduleAsync(user, new { to = new[] { new { name = "", address = "a@example.org" } }, subject = "s" });
        var (noRecipient, _) = await ScheduleAsync(user, new { sendAt = when, subject = "s", text = "t" });
        var (unknownFile, _) = await ScheduleAsync(user, Simple(when, "AAAAAAAAAAAAAAAAAAAAAA"));
        var (badDraft, _) = await ScheduleAsync(user, new { sendAt = when, to = new[] { new { name = "", address = "a@example.org" } }, subject = "s", draftId = "nope" });

        Assert.Equal([HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest], [noTime, noRecipient, unknownFile, badDraft]);
    }

    [Fact]
    public async Task Too_Many_Waiting_Messages_Is_A_Conflict_And_Cancelling_One_Makes_Room()
    {
        var user = await SignInAsync();
        var when = DateTimeOffset.UtcNow.AddHours(1);
        var (_, first) = await ScheduleAsync(user, Simple(when));
        _ = await ScheduleAsync(user, Simple(when.AddHours(1)));

        var (status, body) = await ScheduleAsync(user, Simple(when.AddHours(2)));
        _ = await CallAsync(user, HttpMethod.Delete, "/api/v1/scheduled/" + first.GetProperty("item").GetProperty("id").GetString());
        var (again, _) = await ScheduleAsync(user, Simple(when.AddHours(2)));

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("too_many_scheduled", body.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Created, again);
    }

    [Fact]
    public async Task Cancelling_Puts_The_Message_Back_Among_The_Drafts_And_Takes_It_Out_Of_The_List()
    {
        var user = await SignInAsync();
        var (_, created) = await ScheduleAsync(user, Simple(DateTimeOffset.UtcNow.AddHours(1)));
        var id = created.GetProperty("item").GetProperty("id").GetString();

        var (status, _) = await CallAsync(user, HttpMethod.Delete, "/api/v1/scheduled/" + id);
        var (_, list) = await CallAsync(user, HttpMethod.Get, "/api/v1/scheduled");

        Assert.Equal(HttpStatusCode.NoContent, status);
        Assert.Equal(("Drafts", true), Assert.Single(gateway.Appended));
        Assert.Equal(0, list.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Cancelling_Without_A_Drafts_Folder_Is_A_Conflict_And_The_Message_Stays()
    {
        var user = await SignInAsync();
        var (_, created) = await ScheduleAsync(user, Simple(DateTimeOffset.UtcNow.AddHours(1)));
        gateway.Folders.Clear();

        var (status, body) = await CallAsync(user, HttpMethod.Delete, "/api/v1/scheduled/" + created.GetProperty("item").GetProperty("id").GetString());
        var (_, list) = await CallAsync(user, HttpMethod.Get, "/api/v1/scheduled");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("no_target_folder", body.GetProperty("code").GetString());
        Assert.Equal(1, list.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Nobody_Can_See_Or_Cancel_A_Message_Of_Another_User_Or_One_That_Does_Not_Exist()
    {
        var alice = await SignInAsync();
        var bob = await SignInAsync("bob@vinette.test");
        var (_, created) = await ScheduleAsync(alice, Simple(DateTimeOffset.UtcNow.AddHours(1)));
        var id = created.GetProperty("item").GetProperty("id").GetString();

        var (theirs, _) = await CallAsync(bob, HttpMethod.Delete, "/api/v1/scheduled/" + id);
        var (none, _) = await CallAsync(bob, HttpMethod.Delete, "/api/v1/scheduled/" + new string('a', 32));
        var (_, bobs) = await CallAsync(bob, HttpMethod.Get, "/api/v1/scheduled");
        var (_, alices) = await CallAsync(alice, HttpMethod.Get, "/api/v1/scheduled");

        Assert.Equal([HttpStatusCode.NotFound, HttpStatusCode.NotFound], [theirs, none]);
        Assert.Equal(0, bobs.GetProperty("items").GetArrayLength());
        Assert.Equal(1, alices.GetProperty("items").GetArrayLength());
        Assert.Empty(gateway.Appended);
    }

    [Fact]
    public async Task The_Html_Of_A_Message_Put_Off_Is_Cleaned_Before_It_Is_Kept_And_Too_Deep_Markup_Is_Refused()
    {
        var user = await SignInAsync();
        var when = DateTimeOffset.UtcNow.AddHours(1);

        var (status, _) = await ScheduleAsync(user, new
        {
            sendAt = when,
            to = new[] { new { name = "", address = "anna@example.org" } },
            subject = "s",
            html = "<p style=\"color:red\" onclick=\"alert(1)\">Hi</p><script>alert(1)</script>",
        });
        var (deep, _) = await ScheduleAsync(user, new
        {
            sendAt = when,
            to = new[] { new { name = "", address = "anna@example.org" } },
            subject = "s",
            html = string.Concat(Enumerable.Repeat("<div>", 500)),
        });

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(HttpStatusCode.BadRequest, deep);
        var mail = string.Join(Environment.NewLine, Directory.EnumerateFiles(Path.Combine(directory, "queue"), " *.eml").Select(File.ReadAllText));
        Assert.DoesNotContain("<script", mail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", mail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("color:red", mail, StringComparison.OrdinalIgnoreCase);
    }
}
