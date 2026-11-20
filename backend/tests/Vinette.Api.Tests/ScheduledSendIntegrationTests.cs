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
/// Sending later through the whole application against a real Postfix and Dovecot (VIN-107, ADR-0008): the message waits, goes at its time to the
/// recipient with a copy in "Sent", survives a restart of the application (a new process with the same disk and keys, and no session at all), and,
/// cancelled, goes back among the drafts with its hidden recipients.
/// </summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class ScheduledSendIntegrationTests(DovecotFixture dovecot) : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vinette-scheduled-it-" + Guid.NewGuid().ToString("N"));
    private readonly List<WebApplicationFactory<Program>> factories = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var factory in factories)
        {
            factory.Dispose();
        }

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>An instance of the application; <paramref name="sends"/> says whether it sends what is due (the one that does is the only one that should).</summary>
    private WebApplicationFactory<Program> Instance(bool sends)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Imap:Host", dovecot.Host);
            b.UseSetting("Imap:Port", dovecot.ImapPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
            b.UseSetting("Imap:Security", "None");
            b.UseSetting("Smtp:Host", dovecot.Host);
            b.UseSetting("Smtp:Port", dovecot.SubmissionPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
            b.UseSetting("Smtp:Security", "StartTls");
            b.UseSetting("Smtp:AllowInvalidCertificate", "true");
            b.UseSetting("Attachments:Path", Path.Combine(directory, "files"));
            b.UseSetting("DataProtection:KeysPath", Path.Combine(directory, "keys")); // the keys outlive an instance, as in production
            b.UseSetting("Scheduling:Path", Path.Combine(directory, "queue"));
            b.UseSetting("Scheduling:Enabled", sends ? "true" : "false");
            b.UseSetting("Scheduling:PollInterval", "00:00:01");
            b.UseSetting("Scheduling:MinDelay", "00:00:01");
        });
        factories.Add(factory);
        return factory;
    }

    private sealed record User(HttpClient Http, MailAccount Account, string Session, WebApplicationFactory<Program> Factory);

    private static async Task<User> SignInAsync(WebApplicationFactory<Program> factory, MailAccount account)
    {
        var http = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = await CsrfClient.PostJsonAsync(http, "/api/v1/auth/login", new { address = account.Address, password = account.Password, rememberMe = false }, Token);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(SessionCookie.Name + "=", StringComparison.Ordinal));
        return new User(http, account, cookie[(SessionCookie.Name.Length + 1)..].Split(';')[0], factory);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> CallAsync(User user, HttpMethod method, string url, HttpContent? content = null)
    {
        using var response = await CsrfClient.SendAsync(user.Http, method, url, Token, content, user.Session);
        var text = await response.Content.ReadAsStringAsync(Token);
        return (response.StatusCode, text.Length == 0 ? default : JsonElement.Parse(text));
    }

    private async Task<MailAccount> AccountAsync(string kind) =>
        await dovecot.CreateAccountAsync($"{kind}{Guid.NewGuid():N}"[..14], Token);

    private static async Task<MessagePage> ListAsync(User user, string folder)
    {
        var gateway = user.Factory.Services.GetRequiredService<IMailGateway>();
        var credentials = new MailCredentials("session-" + user.Account.Address, user.Account.Address, user.Account.Password);
        return await gateway.ExecuteAsync(credentials, (session, token) => session.ListMessagesAsync(new MessageListQuery(folder, null, 50), token), Token);
    }

    private static async Task<MessageListItem> WaitForAsync(User user, string folder, string subject)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        while (true)
        {
            if ((await ListAsync(user, folder)).Items.FirstOrDefault(i => i.Subject == subject) is { } found)
            {
                return found;
            }

            await Task.Delay(500, timeout.Token);
        }
    }

    private static async Task WaitUntilQueueIsEmptyAsync(User user)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            var (_, list) = await CallAsync(user, HttpMethod.Get, "/api/v1/scheduled");
            if (list.GetProperty("items").GetArrayLength() == 0)
            {
                return;
            }

            await Task.Delay(300, timeout.Token);
        }
    }

    private static Task<(HttpStatusCode Status, JsonElement Body)> ScheduleAsync(User user, MailAccount to, string subject, DateTimeOffset sendAt) =>
        CallAsync(user, HttpMethod.Post, "/api/v1/scheduled", JsonContent.Create(new
        {
            sendAt,
            to = new[] { new { name = "Боб", address = to.Address } },
            bcc = new[] { new { name = "", address = "hidden@vinette.test" } },
            subject,
            text = "Привет!",
        }));

    [Fact]
    public async Task A_Message_Waits_For_Its_Time_Goes_To_The_Recipient_With_A_Copy_In_Sent_And_Leaves_The_Queue()
    {
        var instance = Instance(sends: true);
        var alice = await SignInAsync(instance, await AccountAsync("snd"));
        var bob = await SignInAsync(instance, await AccountAsync("rcv"));
        var subject = "Позже " + Guid.NewGuid().ToString("N")[..6];

        var (status, _) = await ScheduleAsync(alice, bob.Account, subject, DateTimeOffset.UtcNow.AddSeconds(4));
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.DoesNotContain(subject, (await ListAsync(bob, "INBOX")).Items.Select(i => i.Subject)); // not before its time

        var delivered = await WaitForAsync(bob, "INBOX", subject);

        Assert.Equal(alice.Account.Address, delivered.From?.Address);
        _ = await WaitForAsync(alice, "Sent", subject);
        await WaitUntilQueueIsEmptyAsync(alice); // it leaves the queue a moment after the copy is kept
        Assert.Empty((await ListAsync(alice, "Drafts")).Items);
    }

    [Fact]
    public async Task A_Message_Survives_A_Restart_And_Goes_Without_Anybody_Signed_In()
    {
        var first = Instance(sends: false);
        var alice = await SignInAsync(first, await AccountAsync("snd"));
        var bob = await SignInAsync(first, await AccountAsync("rcv"));
        var subject = "Переживёт " + Guid.NewGuid().ToString("N")[..6];
        var (status, _) = await ScheduleAsync(alice, bob.Account, subject, DateTimeOffset.UtcNow.AddSeconds(4));
        Assert.Equal(HttpStatusCode.Created, status);
        first.Dispose(); // the application stops: its sessions are gone with it

        var second = Instance(sends: true); // a new process, the same disk and keys, nobody signed in
        _ = second.Services; // starts it, and its sender with it
        var bobAgain = new User(bob.Http, bob.Account, bob.Session, second);

        var delivered = await WaitForAsync(bobAgain, "INBOX", subject);

        Assert.Equal(alice.Account.Address, delivered.From?.Address);
    }

    [Fact]
    public async Task Cancelling_Puts_The_Message_Back_Among_The_Drafts_With_Its_Hidden_Recipient_And_It_Never_Goes()
    {
        var instance = Instance(sends: true);
        var alice = await SignInAsync(instance, await AccountAsync("snd"));
        var bob = await SignInAsync(instance, await AccountAsync("rcv"));
        var subject = "Отмена " + Guid.NewGuid().ToString("N")[..6];
        var (_, created) = await ScheduleAsync(alice, bob.Account, subject, DateTimeOffset.UtcNow.AddHours(1));

        var (status, _) = await CallAsync(alice, HttpMethod.Delete, "/api/v1/scheduled/" + created.GetProperty("item").GetProperty("id").GetString());

        Assert.Equal(HttpStatusCode.NoContent, status);
        var draft = await WaitForAsync(alice, "Drafts", subject);
        Assert.True(draft.Draft);
        var gateway = instance.Services.GetRequiredService<IMailGateway>();
        var credentials = new MailCredentials("session-" + alice.Account.Address, alice.Account.Address, alice.Account.Password);
        var source = await gateway.ExecuteAsync(credentials, (session, token) => session.GetMessageAsync(draft.Id, token), Token);
        Assert.Contains("hidden@vinette.test", System.Text.Encoding.UTF8.GetString(source.Content), StringComparison.Ordinal);
        var (_, list) = await CallAsync(alice, HttpMethod.Get, "/api/v1/scheduled");
        Assert.Equal(0, list.GetProperty("items").GetArrayLength());
    }
}
