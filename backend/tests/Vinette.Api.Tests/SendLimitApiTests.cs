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

/// <summary>What one mailbox may send, through the real application (VIN-141): the refusal, its answer, and that a refused message goes nowhere.</summary>
public sealed class SendLimitApiTests : IDisposable
{
    private const string Password = "pw";

    private sealed class AnyoneAuthenticator : IAuthenticator
    {
        public Task<AuthenticationResult> AuthenticateAsync(LoginRequest request, ClientInfo client, CancellationToken cancellationToken) =>
            Task.FromResult(request.Password == Password ? AuthenticationResult.Success : AuthenticationResult.InvalidCredentials);
    }

    private sealed class FakeOutgoing : IOutgoingMail
    {
        public List<OutgoingMessage> Sent { get; } = [];

        public Task<SendOutcome> SendComposedAsync(MailCredentials credentials, ComposedMessage message, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SendOutcome> SendAsync(MailCredentials credentials, OutgoingMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            var envelope = new OutgoingEnvelope(credentials.Address, [.. message.To.Concat(message.Cc).Concat(message.Bcc).Select(a => a.Address)]);
            return Task.FromResult(new SendOutcome(new ComposedMessage(new byte[] { 1 }, envelope, "sent@vinette.test"), SentCopyOutcome.Saved, new MessageId("Sent", 1, 1)));
        }
    }

    private sealed class FakeSuggestions : IRecipientSuggestions
    {
        public Task<IReadOnlyList<MailAddressInfo>> SuggestAsync(MailCredentials credentials, string query, int count, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MailAddressInfo>>([]);

        public Task<IReadOnlyList<KnownRecipient>> LookupAsync(MailCredentials credentials, IReadOnlyList<string> addresses, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KnownRecipient>>([]);

        public void Remember(string mailbox, IEnumerable<MailAddressInfo> recipients)
        {
        }
    }

    private readonly string directory = Path.Combine(Path.GetTempPath(), "vinette-send-limit-" + Guid.NewGuid().ToString("N"));
    private readonly FakeOutgoing outgoing = new();
    private readonly WebApplicationFactory<Program> factory;

    public SendLimitApiTests() =>
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Attachments:Path", directory);
            b.UseSetting("Scheduling:Path", Path.Combine(directory, "scheduled"));
            b.UseSetting("Scheduling:Enabled", "false");
            b.UseSetting("Security:SendRateLimit:MessagesPerHour", "2");
            b.UseSetting("Security:SendRateLimit:RecipientsPerHour", "5");
            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuthenticator>();
                services.AddSingleton<IAuthenticator>(new AnyoneAuthenticator());
                services.RemoveAll<IOutgoingMail>();
                services.AddSingleton<IOutgoingMail>(outgoing);
                services.RemoveAll<IRecipientSuggestions>();
                services.AddSingleton<IRecipientSuggestions>(new FakeSuggestions());
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

    private async Task<(HttpClient Http, string Session)> SignInAsync(string address)
    {
        var http = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = await CsrfClient.PostJsonAsync(http, "/api/v1/auth/login", new { address, password = Password, rememberMe = false }, Token);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(SessionCookie.Name + "=", StringComparison.Ordinal));
        return (http, cookie[(SessionCookie.Name.Length + 1)..].Split(';')[0]);
    }

    private static object Message(params string[] to) => new
    {
        to = to.Select(a => new { name = "", address = a }).ToArray(),
        subject = "Привет",
        text = "Текст",
        attachmentIds = Array.Empty<string>(),
    };

    private static async Task<(HttpStatusCode Status, HttpResponseMessage Response, JsonElement Body)> PostAsync((HttpClient Http, string Session) user, string url, object body)
    {
        var response = await CsrfClient.SendAsync(user.Http, HttpMethod.Post, url, Token, JsonContent.Create(body), user.Session);
        var text = await response.Content.ReadAsStringAsync(Token);
        return (response.StatusCode, response, text.Length == 0 ? default : JsonElement.Parse(text));
    }

    [Fact]
    public async Task The_Message_After_The_Limit_Is_Refused_With_429_A_Code_And_The_Time_To_Wait_And_Is_Not_Sent()
    {
        var alice = await SignInAsync("alice@vinette.test");
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(alice, "/api/v1/messages", Message("a@example.org"))).Status);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(alice, "/api/v1/messages", Message("b@example.org"))).Status);

        var (status, response, body) = await PostAsync(alice, "/api/v1/messages", Message("c@example.org"));

        Assert.Equal(HttpStatusCode.TooManyRequests, status);
        Assert.Equal("send_limit_reached", body.GetProperty("code").GetString());
        var retry = int.Parse(Assert.Single(response.Headers.GetValues("Retry-After")), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(retry, 3500, 3600);
        Assert.Equal(2, outgoing.Sent.Count); // the third one went nowhere
    }

    [Fact]
    public async Task Recipients_Count_Too_The_Same_Address_Once_And_Hidden_Ones_Included()
    {
        var alice = await SignInAsync("alice@vinette.test");
        // 6 different recipients in one message: more than 5 an hour, so it can never pass
        var (status, _, body) = await PostAsync(alice, "/api/v1/messages", Message("1@e.org", "2@e.org", "3@e.org", "4@e.org", "5@e.org", "6@e.org"));
        Assert.Equal(HttpStatusCode.TooManyRequests, status);
        Assert.Equal("send_limit_reached", body.GetProperty("code").GetString());

        // the same address three times is one person
        var (ok, _, _) = await PostAsync(alice, "/api/v1/messages", Message("x@e.org", "X@E.org", "x@e.org"));
        Assert.Equal(HttpStatusCode.OK, ok);
    }

    [Fact]
    public async Task One_Mailbox_Reaching_The_Limit_Does_Not_Stop_Another()
    {
        var alice = await SignInAsync("alice@vinette.test");
        var bob = await SignInAsync("bob@vinette.test");
        for (var i = 0; i < 2; i++)
        {
            _ = await PostAsync(alice, "/api/v1/messages", Message("a@example.org"));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostAsync(alice, "/api/v1/messages", Message("a@example.org"))).Status);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(bob, "/api/v1/messages", Message("a@example.org"))).Status);
    }

    [Fact]
    public async Task A_Request_That_Is_Refused_For_Another_Reason_Does_Not_Use_Up_The_Limit()
    {
        var alice = await SignInAsync("alice@vinette.test");
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(alice, "/api/v1/messages", new { to = Array.Empty<object>(), subject = "x", text = "x", attachmentIds = Array.Empty<string>(), draftId = "not-an-id" })).Status);
        }

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(alice, "/api/v1/messages", Message("a@example.org"))).Status);
    }

    [Fact]
    public async Task A_Message_Put_Off_Counts_When_It_Is_Put_Off_Against_The_Same_Limit()
    {
        var alice = await SignInAsync("alice@vinette.test");
        var later = DateTimeOffset.UtcNow.AddDays(1).ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        object Put(string to) => new { sendAt = later, to = new[] { new { name = "", address = to } }, cc = Array.Empty<object>(), bcc = Array.Empty<object>(), subject = "S", text = "T", attachmentIds = Array.Empty<string>() };
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(alice, "/api/v1/scheduled", Put("a@example.org"))).Status);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(alice, "/api/v1/messages", Message("b@example.org"))).Status);

        var (status, _, body) = await PostAsync(alice, "/api/v1/scheduled", Put("c@example.org"));

        Assert.Equal(HttpStatusCode.TooManyRequests, status);
        Assert.Equal("send_limit_reached", body.GetProperty("code").GetString());
    }
}
