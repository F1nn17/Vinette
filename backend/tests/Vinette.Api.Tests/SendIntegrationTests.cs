using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Vinette.Api.Auth;
using Vinette.Core.Mail;
using Vinette.TestSupport;

namespace Vinette.Api.Tests;

/// <summary>
/// Sending through the whole application against a real Postfix and Dovecot (VIN-138): the message reaches the recipient with its file, a
/// copy is in "Sent", and the uploaded file and the draft it was written from are gone. (How refusals look is covered by
/// <see cref="SendApiTests"/>: the dev Postfix takes an unknown recipient at the door and bounces it later, so it cannot refuse one.)
/// </summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class SendIntegrationTests(DovecotFixture dovecot) : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vinette-send-it-" + Guid.NewGuid().ToString("N"));

    private WebApplicationFactory<Program>? factory;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        factory?.Dispose();
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private WebApplicationFactory<Program> Factory() =>
        factory ??= new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Imap:Host", dovecot.Host);
            b.UseSetting("Imap:Port", dovecot.ImapPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
            b.UseSetting("Imap:Security", "None");
            b.UseSetting("Smtp:Host", dovecot.Host);
            b.UseSetting("Smtp:Port", dovecot.SubmissionPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
            b.UseSetting("Smtp:Security", "StartTls");
            b.UseSetting("Smtp:AllowInvalidCertificate", "true");
            b.UseSetting("Attachments:Path", directory);
        });

    private sealed record User(HttpClient Http, MailAccount Account, string Session);

    private async Task<User> SignInAsync(MailAccount account)
    {
        var http = Factory().CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = await CsrfClient.PostJsonAsync(http, "/api/v1/auth/login", new { address = account.Address, password = account.Password, rememberMe = false }, Token);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(SessionCookie.Name + "=", StringComparison.Ordinal));
        return new User(http, account, cookie[(SessionCookie.Name.Length + 1)..].Split(';')[0]);
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

    private async Task<MessagePage> ListAsync(User user, string folder)
    {
        var gateway = Factory().Services.GetRequiredService<IMailGateway>();
        var credentials = new MailCredentials("session-" + user.Account.Address, user.Account.Address, user.Account.Password);
        return await gateway.ExecuteAsync(credentials, (session, token) => session.ListMessagesAsync(new MessageListQuery(folder, null, 50), token), Token);
    }

    private async Task<MailAccount> AccountAsync(string kind) =>
        await dovecot.CreateAccountAsync($"{kind}{Guid.NewGuid():N}"[..14], Token);

    [Fact]
    public async Task A_Message_Reaches_The_Recipient_With_Its_File_And_The_Draft_And_The_Upload_Are_Gone()
    {
        var alice = await SignInAsync(await AccountAsync("snd"));
        var bob = await SignInAsync(await AccountAsync("rcv"));
        var fileId = await UploadAsync(alice, "смета.pdf", [1, 2, 3, 4, 5]);
        var (draftStatus, draft) = await CallAsync(alice, HttpMethod.Post, "/api/v1/drafts", JsonContent.Create(new
        {
            to = new[] { new { name = "Боб", address = bob.Account.Address } },
            subject = "Смета",
            text = "Черновик",
            attachmentIds = new[] { fileId },
        }));
        Assert.Equal(HttpStatusCode.OK, draftStatus);
        var draftId = draft.GetProperty("id").GetString()!;
        Assert.Single((await ListAsync(alice, "Drafts")).Items);

        var (status, sent) = await CallAsync(alice, HttpMethod.Post, "/api/v1/messages", JsonContent.Create(new
        {
            fromName = "Алиса",
            to = new[] { new { name = "Боб", address = bob.Account.Address } },
            subject = "Смета",
            text = "Смотри вложение.",
            attachmentIds = new[] { fileId },
            draftId,
        }));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("saved", sent.GetProperty("copy").GetString());
        Assert.True(sent.GetProperty("draftRemoved").GetBoolean());
        var copy = Assert.Single((await ListAsync(alice, "Sent")).Items);
        Assert.Equal("Смета", copy.Subject);
        Assert.True(copy.HasAttachments);
        Assert.Empty((await ListAsync(alice, "Drafts")).Items);
        var (_, waiting) = await CallAsync(alice, HttpMethod.Get, "/api/v1/attachments");
        Assert.Equal(0, waiting.GetProperty("items").GetArrayLength());

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        while (true)
        {
            var inbox = await ListAsync(bob, "INBOX");
            if (inbox.Items.Count > 0)
            {
                Assert.Equal("Смета", inbox.Items[0].Subject);
                Assert.True(inbox.Items[0].HasAttachments);
                break;
            }

            await Task.Delay(300, timeout.Token);
        }
    }
}
