using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Vinette.Api.Auth;
using Vinette.Core.Mail;
using Vinette.TestSupport;

namespace Vinette.Api.Tests;

/// <summary>What the editor needs to answer or forward a message, from a real Dovecot (VIN-102): the headers of the conversation, the recipients, the body in its own encoding, and (for a forward) the files among the uploaded ones.</summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class QuoteIntegrationTests(DovecotFixture dovecot) : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vinette-quote-it-" + Guid.NewGuid().ToString("N"));

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

    private static async Task<(HttpStatusCode Status, JsonElement Body)> CallAsync(User user, string url)
    {
        using var response = await CsrfClient.SendAsync(user.Http, HttpMethod.Get, url, Token, null, user.Session);
        var text = await response.Content.ReadAsStringAsync(Token);
        return (response.StatusCode, text.Length == 0 ? default : JsonElement.Parse(text));
    }

    private async Task<(User User, string Id)> MailboxWithMessageAsync()
    {
        var account = await dovecot.CreateAccountAsync($"quo{Guid.NewGuid():N}"[..14], Token);
        var user = await SignInAsync(account);
        var raw = string.Join(
            "\r\n",
            "From: =?utf-8?B?0JDQvdC90LAg0KHQvNC40YDQvdC+0LLQsA==?= <anna@example.org>",
            "To: " + account.Address + ", Boris <boris@example.org>",
            "Cc: carol@example.org",
            "Reply-To: =?utf-8?B?0JvQuNGH0L3Ri9C5?= <anna.private@example.org>",
            "Subject: =?utf-8?B?0KHQvNC10YLQsA==?=",
            "Date: Tue, 17 Nov 2026 09:00:00 +0000",
            "Message-ID: <m3@example.org>",
            "In-Reply-To: <m2@example.org>",
            "References: <m1@example.org> <m2@example.org>",
            "MIME-Version: 1.0",
            "Content-Type: multipart/mixed; boundary=\"b\"",
            string.Empty,
            "--b",
            "Content-Type: text/plain; charset=utf-8",
            "Content-Transfer-Encoding: base64",
            string.Empty,
            Convert.ToBase64String(Encoding.UTF8.GetBytes("Привет, смотри файл.")),
            "--b",
            "Content-Type: application/pdf; name=\"report.pdf\"",
            "Content-Disposition: attachment; filename=\"report.pdf\"",
            "Content-Transfer-Encoding: base64",
            string.Empty,
            Convert.ToBase64String([1, 2, 3, 4, 5]),
            "--b--",
            string.Empty);
        var gateway = Factory().Services.GetRequiredService<IMailGateway>();
        var credentials = new MailCredentials("session-" + account.Address, account.Address, account.Password);
        _ = await gateway.ExecuteAsync(
            credentials,
            (session, token) => session.AppendMessageAsync("INBOX", Encoding.ASCII.GetBytes(raw), false, false, DateTimeOffset.UtcNow, token),
            Token);
        var page = await gateway.ExecuteAsync(credentials, (session, token) => session.ListMessagesAsync(new MessageListQuery("INBOX", null, 10), token), Token);
        return (user, page.Items.Single().Id.ToString());
    }

    [Fact]
    public async Task The_Quote_Has_The_Headers_Of_The_Conversation_The_Recipients_And_The_Decoded_Body_But_No_Files()
    {
        var (user, id) = await MailboxWithMessageAsync();

        var (status, quote) = await CallAsync(user, $"/api/v1/messages/{id}/quote");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("m3@example.org", quote.GetProperty("messageId").GetString());
        Assert.Equal(["m1@example.org", "m2@example.org"], quote.GetProperty("references").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal("Анна Смирнова", quote.GetProperty("from").GetProperty("name").GetString());
        Assert.Equal("anna.private@example.org", quote.GetProperty("replyTo")[0].GetProperty("address").GetString());
        Assert.Equal(2, quote.GetProperty("to").GetArrayLength());
        Assert.Equal("carol@example.org", quote.GetProperty("cc")[0].GetProperty("address").GetString());
        Assert.Equal("Смета", quote.GetProperty("subject").GetString());
        Assert.Equal("Привет, смотри файл.", quote.GetProperty("text").GetString()!.Trim());
        Assert.Equal(0, quote.GetProperty("attachments").GetArrayLength());
        var (_, waiting) = await CallAsync(user, "/api/v1/attachments");
        Assert.Equal(0, waiting.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task For_A_Forward_The_Files_Of_The_Message_Are_Put_Among_The_Uploaded_Ones()
    {
        var (user, id) = await MailboxWithMessageAsync();

        var (status, quote) = await CallAsync(user, $"/api/v1/messages/{id}/quote?attachments=true");

        Assert.Equal(HttpStatusCode.OK, status);
        var file = Assert.Single(quote.GetProperty("attachments").EnumerateArray());
        Assert.Equal("report.pdf", file.GetProperty("fileName").GetString());
        Assert.Equal(5, file.GetProperty("size").GetInt64());
        var (_, waiting) = await CallAsync(user, "/api/v1/attachments");
        Assert.Equal(file.GetProperty("id").GetString(), Assert.Single(waiting.GetProperty("items").EnumerateArray()).GetProperty("id").GetString());
    }

    [Fact]
    public async Task A_Message_That_Is_Not_There_Is_A_404()
    {
        var (user, id) = await MailboxWithMessageAsync();
        var real = MessageId.Parse(id, null);

        var (status, _) = await CallAsync(user, $"/api/v1/messages/{new MessageId(real.Folder, real.UidValidity, 9999)}/quote");

        Assert.Equal(HttpStatusCode.NotFound, status);
    }
}
