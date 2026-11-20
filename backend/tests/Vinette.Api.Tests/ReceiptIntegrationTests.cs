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
/// Read receipts through the whole application against a real Postfix and Dovecot (VIN-106): a message that asks for one is delivered with the
/// request, the reader is asked, and only the reader's answer sends the receipt (or declines it); either way the question is not asked again.
/// </summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class ReceiptIntegrationTests(DovecotFixture dovecot) : IDisposable
{
    private WebApplicationFactory<Program>? factory;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => factory?.Dispose();

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

    private async Task<MailAccount> AccountAsync(string kind) =>
        await dovecot.CreateAccountAsync($"{kind}{Guid.NewGuid():N}"[..14], Token);

    private async Task<MessagePage> ListAsync(User user, string folder)
    {
        var gateway = Factory().Services.GetRequiredService<IMailGateway>();
        var credentials = new MailCredentials("session-" + user.Account.Address, user.Account.Address, user.Account.Password);
        return await gateway.ExecuteAsync(credentials, (session, token) => session.ListMessagesAsync(new MessageListQuery(folder, null, 50), token), Token);
    }

    private async Task<MessageListItem> WaitForAsync(User user, string folder, string subject)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        while (true)
        {
            var page = await ListAsync(user, folder);
            if (page.Items.FirstOrDefault(i => i.Subject == subject) is { } found)
            {
                return found;
            }

            await Task.Delay(300, timeout.Token);
        }
    }

    private async Task<(User Alice, User Bob, string Id)> DeliveredAsync(bool requestReceipt, string subject)
    {
        var alice = await SignInAsync(await AccountAsync("snd"));
        var bob = await SignInAsync(await AccountAsync("rcv"));
        var (status, _) = await CallAsync(alice, HttpMethod.Post, "/api/v1/messages", JsonContent.Create(new
        {
            to = new[] { new { name = "Боб", address = bob.Account.Address } },
            subject,
            text = "Привет!",
            requestReceipt,
        }));
        Assert.Equal(HttpStatusCode.OK, status);
        var delivered = await WaitForAsync(bob, "INBOX", subject);
        return (alice, bob, delivered.Id.ToString());
    }

    [Fact]
    public async Task A_Message_That_Asks_For_A_Receipt_Is_Shown_With_The_Question_And_One_Answer_Sends_It_Once()
    {
        var subject = "Подтвердите " + Guid.NewGuid().ToString("N")[..6];
        var (alice, bob, id) = await DeliveredAsync(requestReceipt: true, subject);
        var (_, before) = await CallAsync(bob, HttpMethod.Get, $"/api/v1/messages/{id}");
        Assert.True(before.GetProperty("receiptRequested").GetBoolean());
        Assert.Empty((await ListAsync(alice, "INBOX")).Items); // nothing was sent by opening it

        var (status, _) = await CallAsync(bob, HttpMethod.Post, $"/api/v1/messages/{id}/receipt", JsonContent.Create(new { send = true }));

        Assert.Equal(HttpStatusCode.NoContent, status);
        var receipt = await WaitForAsync(alice, "INBOX", "Read: " + subject);
        Assert.Equal(bob.Account.Address, receipt.From?.Address);
        var (_, after) = await CallAsync(bob, HttpMethod.Get, $"/api/v1/messages/{id}");
        Assert.False(after.GetProperty("receiptRequested").GetBoolean()); // not asked again
        var (again, _) = await CallAsync(bob, HttpMethod.Post, $"/api/v1/messages/{id}/receipt", JsonContent.Create(new { send = true }));
        Assert.Equal(HttpStatusCode.BadRequest, again); // and not sent twice
    }

    [Fact]
    public async Task Declining_Sends_Nothing_And_Is_Not_Asked_Again()
    {
        var subject = "Не нужно " + Guid.NewGuid().ToString("N")[..6];
        var (alice, bob, id) = await DeliveredAsync(requestReceipt: true, subject);

        var (status, _) = await CallAsync(bob, HttpMethod.Post, $"/api/v1/messages/{id}/receipt", JsonContent.Create(new { send = false }));

        Assert.Equal(HttpStatusCode.NoContent, status);
        var (_, after) = await CallAsync(bob, HttpMethod.Get, $"/api/v1/messages/{id}");
        Assert.False(after.GetProperty("receiptRequested").GetBoolean());
        await Task.Delay(TimeSpan.FromSeconds(3), Token); // time for a receipt to arrive, if one had been sent
        Assert.Empty((await ListAsync(alice, "INBOX")).Items);
    }

    [Fact]
    public async Task A_Message_That_Does_Not_Ask_Has_No_Question_And_Cannot_Be_Answered()
    {
        var subject = "Без запроса " + Guid.NewGuid().ToString("N")[..6];
        var (_, bob, id) = await DeliveredAsync(requestReceipt: false, subject);

        var (_, detail) = await CallAsync(bob, HttpMethod.Get, $"/api/v1/messages/{id}");
        var (status, _) = await CallAsync(bob, HttpMethod.Post, $"/api/v1/messages/{id}/receipt", JsonContent.Create(new { send = true }));

        Assert.False(detail.GetProperty("receiptRequested").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }
}
