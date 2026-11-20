using System.Net;
using System.Net.Http.Json;
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

/// <summary>The send endpoint through the real application (VIN-138), with stand-ins for sending, the drafts folder and the suggestions: what is sent, what is tidied after it, and what each refusal looks like.</summary>
public sealed class SendApiTests : IDisposable
{
    private const string Password = "pw";
    private static readonly MessageId DraftId = new("Drafts", 3, 12);
    private static readonly MessageId CopyId = new("Sent", 5, 40);
    private static readonly string[] ThreadSoFar = ["first@example.org"];

    private sealed class AnyoneAuthenticator : IAuthenticator
    {
        public Task<AuthenticationResult> AuthenticateAsync(LoginRequest request, ClientInfo client, CancellationToken cancellationToken) =>
            Task.FromResult(request.Password == Password ? AuthenticationResult.Success : AuthenticationResult.InvalidCredentials);
    }

    private sealed class FakeOutgoing : IOutgoingMail
    {
        public List<OutgoingMessage> Sent { get; } = [];

        public Exception? Fail { get; set; }

        public SentCopyOutcome Copy { get; set; } = SentCopyOutcome.Saved;

        public Task<SendOutcome> SendAsync(MailCredentials credentials, OutgoingMessage message, CancellationToken cancellationToken)
        {
            if (Fail is not null)
            {
                throw Fail;
            }

            Sent.Add(message);
            var envelope = new OutgoingEnvelope(credentials.Address, [.. message.To.Concat(message.Cc).Concat(message.Bcc).Select(a => a.Address)]);
            return Task.FromResult(new SendOutcome(new ComposedMessage(new byte[] { 1 }, envelope, "sent-1@vinette.test"), Copy, Copy == SentCopyOutcome.Saved ? CopyId : null));
        }
    }

    private sealed class FakeDrafts : IDrafts
    {
        public List<MessageId> Deleted { get; } = [];

        public Exception? FailDelete { get; set; }

        public Task<SavedDraft?> SaveAsync(MailCredentials credentials, OutgoingMessage message, MessageId? previous, CancellationToken cancellationToken) =>
            Task.FromResult<SavedDraft?>(null);

        public Task<OpenedDraft?> OpenAsync(MailCredentials credentials, MessageId id, CancellationToken cancellationToken) =>
            Task.FromResult<OpenedDraft?>(null);

        public Task<bool> DeleteAsync(MailCredentials credentials, MessageId id, CancellationToken cancellationToken)
        {
            if (FailDelete is not null)
            {
                throw FailDelete;
            }

            Deleted.Add(id);
            return Task.FromResult(true);
        }
    }

    private sealed class FakeSuggestions : IRecipientSuggestions
    {
        public List<(string Mailbox, MailAddressInfo[] People)> Remembered { get; } = [];

        public Task<IReadOnlyList<MailAddressInfo>> SuggestAsync(MailCredentials credentials, string query, int count, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MailAddressInfo>>([]);

        public Task<IReadOnlyList<KnownRecipient>> LookupAsync(MailCredentials credentials, IReadOnlyList<string> addresses, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KnownRecipient>>([.. addresses.Select(a => new KnownRecipient(a, null))]);

        public void Remember(string mailbox, IEnumerable<MailAddressInfo> recipients) => Remembered.Add((mailbox, [.. recipients]));
    }

    private readonly string directory = Path.Combine(Path.GetTempPath(), "vinette-send-api-" + Guid.NewGuid().ToString("N"));
    private readonly FakeOutgoing outgoing = new();
    private readonly FakeDrafts drafts = new();
    private readonly FakeSuggestions suggestions = new();
    private readonly WebApplicationFactory<Program> factory;

    public SendApiTests() =>
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Attachments:Path", directory);
            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuthenticator>();
                services.AddSingleton<IAuthenticator>(new AnyoneAuthenticator());
                services.RemoveAll<IOutgoingMail>();
                services.AddSingleton<IOutgoingMail>(outgoing);
                services.RemoveAll<IDrafts>();
                services.AddSingleton<IDrafts>(drafts);
                services.RemoveAll<IRecipientSuggestions>();
                services.AddSingleton<IRecipientSuggestions>(suggestions);
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

    private static Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(User user, object request) =>
        CallAsync(user, HttpMethod.Post, "/api/v1/messages", JsonContent.Create(request));

    private static object Simple(params string[] attachmentIds) => new
    {
        to = new[] { new { name = "Анна", address = "anna@example.org" } },
        subject = "Привет",
        text = "Текст",
        attachmentIds,
    };

    private static async Task<int> WaitingFilesAsync(User user)
    {
        var (_, body) = await CallAsync(user, HttpMethod.Get, "/api/v1/attachments");
        return body.GetProperty("items").GetArrayLength();
    }

    [Fact]
    public async Task Sending_Needs_A_Session()
    {
        using var http = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        using var response = await CsrfClient.PostJsonAsync(http, "/api/v1/messages", Simple(), Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(outgoing.Sent);
    }

    [Fact]
    public async Task What_Was_Written_Is_Sent_As_Written_From_The_Signed_In_Mailbox_With_The_Uploaded_Files()
    {
        var user = await SignInAsync();
        var id = await UploadAsync(user, "смета.pdf", [1, 2, 3, 4]);

        var (status, body) = await SendAsync(user, new
        {
            fromName = "Алиса",
            to = new[] { new { name = "Анна", address = "anna@example.org" } },
            cc = new[] { new { name = "", address = "boris@example.org" } },
            bcc = new[] { new { name = "", address = "hidden@example.org" } },
            subject = "Привет",
            text = "Текст",
            html = "<p>Текст</p>",
            attachmentIds = new[] { id, id },
            inReplyTo = "first@example.org",
            references = ThreadSoFar,
        });

        Assert.Equal(HttpStatusCode.OK, status);
        var sent = Assert.Single(outgoing.Sent);
        Assert.Equal(new MailAddressInfo("Алиса", "alice@vinette.test"), sent.From);
        Assert.Equal("anna@example.org", Assert.Single(sent.To).Address);
        Assert.Equal("boris@example.org", Assert.Single(sent.Cc).Address);
        Assert.Equal("hidden@example.org", Assert.Single(sent.Bcc).Address);
        Assert.Equal(("Привет", "Текст", "<p>Текст</p>"), (sent.Subject, sent.Text, sent.Html));
        var file = Assert.Single(sent.Attachments);
        Assert.Equal("смета.pdf", file.FileName);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, file.Content.ToArray());
        Assert.Equal("first@example.org", sent.InReplyTo);
        Assert.Equal(ThreadSoFar, sent.References);
        Assert.Equal("sent-1@vinette.test", body.GetProperty("messageId").GetString());
        Assert.Equal("saved", body.GetProperty("copy").GetString());
        Assert.Equal(CopyId.ToString(), body.GetProperty("copyId").GetString());
        Assert.True(body.GetProperty("draftRemoved").GetBoolean());
    }

    [Fact]
    public async Task Once_It_Is_Out_The_Files_The_Draft_Are_Gone_And_The_Recipients_Are_Remembered()
    {
        var user = await SignInAsync();
        var id = await UploadAsync(user, "a.pdf", [1]);
        Assert.Equal(1, await WaitingFilesAsync(user));

        var (status, _) = await SendAsync(user, new
        {
            to = new[] { new { name = "Анна", address = "anna@example.org" } },
            bcc = new[] { new { name = "", address = "hidden@example.org" } },
            subject = "s",
            text = "t",
            attachmentIds = new[] { id },
            draftId = DraftId.ToString(),
        });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, await WaitingFilesAsync(user));
        Assert.Equal(DraftId, Assert.Single(drafts.Deleted));
        var (mailbox, people) = Assert.Single(suggestions.Remembered);
        Assert.Equal("alice@vinette.test", mailbox);
        Assert.Equal(["anna@example.org", "hidden@example.org"], people.Select(p => p.Address));
    }

    [Theory]
    [InlineData(SentCopyOutcome.NoSentFolder, "no_sent_folder")]
    [InlineData(SentCopyOutcome.Failed, "failed")]
    public async Task A_Copy_That_Was_Not_Kept_Is_Told_But_The_Message_Still_Went_Out(SentCopyOutcome copy, string expected)
    {
        outgoing.Copy = copy;
        var user = await SignInAsync();

        var (status, body) = await SendAsync(user, Simple());

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(expected, body.GetProperty("copy").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("copyId").ValueKind);
    }

    [Fact]
    public async Task A_Draft_That_Cannot_Be_Deleted_Does_Not_Fail_A_Message_That_Went_Out()
    {
        drafts.FailDelete = new MailUnavailableException("down");
        var user = await SignInAsync();

        var (status, body) = await SendAsync(user, new
        {
            to = new[] { new { name = "", address = "anna@example.org" } },
            subject = "s",
            text = "t",
            draftId = DraftId.ToString(),
        });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body.GetProperty("draftRemoved").GetBoolean());
        Assert.Single(outgoing.Sent);
    }

    [Theory]
    [InlineData(SendRejection.Sender, HttpStatusCode.UnprocessableEntity, "send_refused")]
    [InlineData(SendRejection.Content, HttpStatusCode.UnprocessableEntity, "send_refused")]
    [InlineData(SendRejection.Other, HttpStatusCode.UnprocessableEntity, "send_refused")]
    [InlineData(SendRejection.TooLarge, HttpStatusCode.RequestEntityTooLarge, "send_too_large")]
    public async Task A_Refusal_Of_The_Mail_Server_Is_A_Clear_Problem_And_Nothing_Is_Tidied(SendRejection reason, HttpStatusCode expectedStatus, string expectedCode)
    {
        outgoing.Fail = new MailRejectedException(reason, "no");
        var user = await SignInAsync();
        var id = await UploadAsync(user, "a.pdf", [1]);

        var (status, body) = await SendAsync(user, new
        {
            to = new[] { new { name = "", address = "anna@example.org" } },
            subject = "s",
            text = "t",
            attachmentIds = new[] { id },
            draftId = DraftId.ToString(),
        });

        Assert.Equal(expectedStatus, status);
        Assert.Equal(expectedCode, body.GetProperty("code").GetString());
        Assert.Equal(1, await WaitingFilesAsync(user)); // the user can fix the problem and send again
        Assert.Empty(drafts.Deleted);
        Assert.Empty(suggestions.Remembered);
    }

    [Fact]
    public async Task A_Refused_Recipient_Is_Named_So_That_The_User_Can_Fix_It()
    {
        outgoing.Fail = new MailRejectedException(SendRejection.Recipient, "no") { Address = "nobody@example.org" };
        var user = await SignInAsync();

        var (status, body) = await SendAsync(user, Simple());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("recipient_rejected", body.GetProperty("code").GetString());
        Assert.Equal("nobody@example.org", body.GetProperty("address").GetString());
    }

    [Fact]
    public async Task A_Connection_That_Broke_After_The_Message_Was_Handed_Over_Says_It_May_Have_Gone()
    {
        outgoing.Fail = new MailUnavailableException("broke", new IOException("x"), messageMayHaveBeenSent: true);
        var user = await SignInAsync();

        var (status, body) = await SendAsync(user, new
        {
            to = new[] { new { name = "", address = "anna@example.org" } },
            subject = "s",
            text = "t",
            draftId = DraftId.ToString(),
        });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("send_uncertain", body.GetProperty("code").GetString());
        Assert.Empty(drafts.Deleted); // the draft stays until the user knows
        Assert.Empty(suggestions.Remembered);
    }

    [Fact]
    public async Task A_Mail_Server_That_Cannot_Be_Reached_Is_Reported_As_Such()
    {
        outgoing.Fail = new MailUnavailableException("down");
        var user = await SignInAsync();

        var (status, body) = await SendAsync(user, Simple());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("mail_unavailable", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task What_Cannot_Be_Built_Is_A_Bad_Request()
    {
        outgoing.Fail = new ArgumentException("A message needs at least one recipient.");
        var user = await SignInAsync();

        var (status, body) = await SendAsync(user, new { subject = "s", text = "t" });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("bad_request", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_Unknown_File_Or_A_Draft_That_Is_Not_An_Id_Is_A_Bad_Request_And_Nothing_Is_Sent()
    {
        var user = await SignInAsync();

        var (unknownFile, _) = await SendAsync(user, Simple("AAAAAAAAAAAAAAAAAAAAAA"));
        var (badDraft, _) = await SendAsync(user, new
        {
            to = new[] { new { name = "", address = "anna@example.org" } },
            subject = "s",
            draftId = "not-an-id",
        });

        Assert.Equal(HttpStatusCode.BadRequest, unknownFile);
        Assert.Equal(HttpStatusCode.BadRequest, badDraft);
        Assert.Empty(outgoing.Sent);
    }

    [Fact]
    public async Task A_File_Of_Another_Mailbox_Cannot_Be_Sent()
    {
        var alice = await SignInAsync();
        var bob = await SignInAsync("bob@vinette.test");
        var id = await UploadAsync(alice, "private.pdf", [9]);

        var (status, _) = await SendAsync(bob, Simple(id));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Empty(outgoing.Sent);
        Assert.Equal(1, await WaitingFilesAsync(alice));
    }
}
