using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MimeKit;
using Vinette.Api.Auth;
using Vinette.Api.Hubs;
using Vinette.Api.Sessions;
using Vinette.Core.Mail;
using Vinette.TestSupport;

namespace Vinette.Api.Tests;

/// <summary>
/// The real-time channel through the real application against a real Dovecot (VIN-083): needs a session, sends what happens in
/// the inbox to the connections of that mailbox only, and closes when the session ends. A small JSON-protocol client over the
/// test server's WebSocket stands in for the browser.
/// </summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class MailHubTests(DovecotFixture dovecot) : IDisposable
{
    private readonly List<WebApplicationFactory<Program>> factories = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => factories.ForEach(f => f.Dispose());

    private sealed record User(MailAccount Account, string Cookie, HttpClient Http, string Session);

    private async Task<WebApplicationFactory<Program>> StartAsync(TimeSpan? sessionCheck = null)
    {
        var web = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Imap:Host", dovecot.Host);
            b.UseSetting("Imap:Port", dovecot.ImapPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
            b.UseSetting("Imap:Security", "None");
            b.UseSetting("Imap:IdleGrace", "00:00:01");
            if (sessionCheck is { } interval)
            {
                b.ConfigureTestServices(services => services.AddSingleton(provider => new MailHubConnections(
                    provider.GetRequiredService<IMailboxWatcher>(),
                    provider.GetRequiredService<IHubContext<MailHub>>(),
                    provider.GetRequiredService<SessionService>(),
                    provider.GetRequiredService<ILogger<MailHubConnections>>(),
                    interval)));
            }
        });
        factories.Add(web);
        await Task.CompletedTask;
        return web;
    }

    private async Task<User> SignInAsync(WebApplicationFactory<Program> web)
    {
        var account = await dovecot.CreateAccountAsync($"hub{Guid.NewGuid():N}"[..14], Token);
        var http = web.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = await CsrfClient.PostJsonAsync(http, "/api/v1/auth/login", new { address = account.Address, password = account.Password, rememberMe = false }, Token);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(SessionCookie.Name + "=", StringComparison.Ordinal)).Split(';')[0];
        return new User(account, cookie, http, cookie[(SessionCookie.Name.Length + 1)..]);
    }

    private static Task<HubSocket> ConnectAsync(WebApplicationFactory<Program> web, User user, bool negotiate = true) =>
        HubSocket.ConnectAsync(web, user.Cookie, user.Http, negotiate);

    private async Task DeliverAsync(MailAccount to, string subject = "Hello from the world")
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Anna", "anna@vinette.test"));
        message.To.Add(MailboxAddress.Parse(to.Address));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = "Hi" };
        using var smtp = new SmtpClient { LocalDomain = "client.vinette.test" };
        await smtp.ConnectAsync(dovecot.Host, dovecot.SmtpPort, SecureSocketOptions.None, Token);
        await smtp.SendAsync(new MailKit.SendRequest(message), Token);
        await smtp.DisconnectAsync(quit: true, Token);
    }

    private static readonly TimeSpan Long = TimeSpan.FromSeconds(45);

    [Fact]
    public async Task Mail_That_Arrives_Reaches_The_Connection_As_An_Event_With_Its_Line_Of_The_List()
    {
        var web = await StartAsync();
        var user = await SignInAsync(web);
        using var hub = await ConnectAsync(web, user);
        await Task.Delay(2000, Token); // the watching connection settles into its wait

        await DeliverAsync(user.Account);
        var arrived = await hub.NextEventAsync("arrived", Long);

        Assert.NotNull(arrived);
        Assert.Equal("INBOX", arrived.Value.GetProperty("folder").GetString());
        Assert.Equal("Hello from the world", arrived.Value.GetProperty("message").GetProperty("subject").GetString());
        Assert.Equal("anna@vinette.test", arrived.Value.GetProperty("message").GetProperty("from").GetProperty("address").GetString());
        var counters = await hub.NextEventAsync("counters", Long);
        Assert.NotNull(counters);
        Assert.Equal(1, counters.Value.GetProperty("unread").GetInt32());
        Assert.DoesNotContain(user.Account.Password, arrived.Value.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_Connection_Of_The_Mailbox_Gets_The_Event_And_One_Closing_Does_Not_Stop_The_Other()
    {
        var web = await StartAsync();
        var user = await SignInAsync(web);
        using var phone = await ConnectAsync(web, user);
        using var computer = await ConnectAsync(web, user);
        await Task.Delay(2000, Token);

        await DeliverAsync(user.Account, "First");
        Assert.NotNull(await phone.NextEventAsync("arrived", Long));
        Assert.NotNull(await computer.NextEventAsync("arrived", Long));

        await phone.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", Token);
        await Task.Delay(1500, Token);
        await DeliverAsync(user.Account, "Second");

        var second = await computer.NextEventAsync("arrived", Long);
        Assert.NotNull(second);
        Assert.Equal("Second", second.Value.GetProperty("message").GetProperty("subject").GetString());
    }

    [Fact]
    public async Task Another_Mailboxs_Mail_Is_Not_Sent_To_A_Connection()
    {
        var web = await StartAsync();
        var alice = await SignInAsync(web);
        var bob = await SignInAsync(web);
        using var aliceHub = await ConnectAsync(web, alice);
        using var bobHub = await ConnectAsync(web, bob);
        await Task.Delay(2000, Token);

        await DeliverAsync(bob.Account);

        Assert.NotNull(await bobHub.NextEventAsync("arrived", Long));
        Assert.Null(await aliceHub.NextEventAsync("arrived", TimeSpan.FromSeconds(4)));
    }

    [Fact]
    public async Task Signing_Out_Closes_The_Connection()
    {
        var web = await StartAsync(sessionCheck: TimeSpan.FromMilliseconds(300));
        var user = await SignInAsync(web);
        using var hub = await ConnectAsync(web, user);

        var response = await CsrfClient.SendAsync(user.Http, HttpMethod.Post, "/api/v1/auth/logout", Token, null, user.Session);
        Assert.True(response.IsSuccessStatusCode);

        // the hub says it is closing (message type 7), then the socket ends
        var closing = await hub.NextAsync(TimeSpan.FromSeconds(15));
        Assert.True(closing is null || closing.Value.GetProperty("type").GetInt32() == 7);
        Assert.Null(await hub.NextAsync(TimeSpan.FromSeconds(15)));
        var connections = web.Services.GetRequiredService<MailHubConnections>();
        await WaitUntilAsync(() => connections.Count == 0);
    }

    [Fact]
    public async Task A_Connection_Opened_At_Once_Without_Negotiation_Works_Like_Any_Other()
    {
        // The page connects so (VIN-088): no instance of the application has to be the same one for two requests.
        var web = await StartAsync();
        var user = await SignInAsync(web);
        using var hub = await ConnectAsync(web, user, negotiate: false);
        await Task.Delay(2000, Token);

        await DeliverAsync(user.Account);

        var arrived = await hub.NextEventAsync("arrived", Long);
        Assert.NotNull(arrived);
        Assert.Equal("Hello from the world", arrived.Value.GetProperty("message").GetProperty("subject").GetString());
    }

    [Fact]
    public async Task A_WebSocket_Opened_At_Once_Without_A_Session_Is_Refused()
    {
        var web = await StartAsync();
        var client = web.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers["Origin"] = CsrfClient.Origin;

        var failure = await Record.ExceptionAsync(() => client.ConnectAsync(new Uri("ws://localhost/hubs/mail"), Token));

        Assert.NotNull(failure); // 401 on the upgrade request
    }

    [Fact]
    public async Task A_Connection_Without_A_Session_Is_Refused()
    {
        var web = await StartAsync();
        var http = web.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/hubs/mail/negotiate?negotiateVersion=1");
        request.Headers.Add("Origin", CsrfClient.Origin);
        var answer = await http.SendAsync(request, Token);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, answer.StatusCode);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (!condition())
        {
            await Task.Delay(50, timeout.Token);
        }
    }
}
