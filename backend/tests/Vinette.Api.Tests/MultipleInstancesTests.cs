using System.Net;
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
/// Two instances of the application behind one address (VIN-088, ADR-0007), against a real Dovecot and a real Redis: the sessions are
/// in Redis, the keys of Data Protection and the settings are in shared directories, and the real-time channel is a plain WebSocket
/// that any instance can take, so that nothing has to route a client to the instance it came from.
/// </summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class MultipleInstancesTests(DovecotFixture dovecot) : IAsyncLifetime
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly RedisFixture redis = new();
    private readonly string shared = Path.Combine(Path.GetTempPath(), "vinette-instances-" + Guid.NewGuid().ToString("N"));
    private readonly string prefix = $"instances-{Guid.NewGuid():N}:";
    private readonly List<WebApplicationFactory<Program>> instances = [];

    public async ValueTask InitializeAsync() => await redis.InitializeAsync();

    public async ValueTask DisposeAsync()
    {
        instances.ForEach(i => i.Dispose());
        await redis.DisposeAsync();
        if (Directory.Exists(shared))
        {
            Directory.Delete(shared, recursive: true);
        }
    }

    /// <summary>One more instance of the application, configured like the others of the same installation.</summary>
    private WebApplicationFactory<Program> StartInstance(TimeSpan? sessionCheck = null)
    {
        var web = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Imap:Host", dovecot.Host);
            b.UseSetting("Imap:Port", dovecot.ImapPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
            b.UseSetting("Imap:Security", "None");
            b.UseSetting("Imap:IdleGrace", "00:00:01");
            b.UseSetting("Session:Store", "Redis");
            b.UseSetting("Redis:ConnectionString", redis.ConnectionString);
            b.UseSetting("Redis:KeyPrefix", prefix);
            b.UseSetting("DataProtection:KeysPath", Path.Combine(shared, "keys"));
            b.UseSetting("Settings:Path", Path.Combine(shared, "settings"));
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
        instances.Add(web);
        return web;
    }

    /// <summary>Signs a new user in on one instance; the cookie is what a browser would carry to any instance.</summary>
    private async Task<(MailAccount Account, string Cookie, string Session, HttpClient Http)> SignInAsync(WebApplicationFactory<Program> instance)
    {
        var account = await dovecot.CreateAccountAsync($"ins{Guid.NewGuid():N}"[..14], Token);
        var http = instance.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = await CsrfClient.PostJsonAsync(http, "/api/v1/auth/login", new { address = account.Address, password = account.Password, rememberMe = false }, Token);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(SessionCookie.Name + "=", StringComparison.Ordinal)).Split(';')[0];
        return (account, cookie, cookie[(SessionCookie.Name.Length + 1)..], http);
    }

    private async Task DeliverAsync(MailAccount to)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Anna", "anna@vinette.test"));
        message.To.Add(MailboxAddress.Parse(to.Address));
        message.Subject = "Hello from the world";
        message.Body = new TextPart("plain") { Text = "Hi" };
        using var smtp = new SmtpClient { LocalDomain = "client.vinette.test" };
        await smtp.ConnectAsync(dovecot.Host, dovecot.SmtpPort, SecureSocketOptions.None, Token);
        await smtp.SendAsync(new MailKit.SendRequest(message), Token);
        await smtp.DisconnectAsync(quit: true, Token);
    }

    private static readonly TimeSpan Long = TimeSpan.FromSeconds(45);

    [Fact]
    public async Task A_Request_With_The_Cookie_Of_One_Instance_Is_Served_By_Another()
    {
        var first = StartInstance();
        var second = StartInstance();
        var user = await SignInAsync(first);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/folders");
        request.Headers.Add("Cookie", user.Cookie);
        var response = await second.CreateClient().SendAsync(request, Token);

        // the session is in Redis, and the password in it can be read here: the keys of Data Protection are shared
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_Channel_Opened_On_Another_Instance_Than_The_One_That_Signed_The_User_In_Gets_The_Events()
    {
        var first = StartInstance();
        var second = StartInstance();
        var user = await SignInAsync(first);
        using var hub = await HubSocket.ConnectAsync(second, user.Cookie, second.CreateClient(), negotiate: false);
        await Task.Delay(2000, Token);

        await DeliverAsync(user.Account);

        Assert.NotNull(await hub.NextEventAsync("arrived", Long));
    }

    [Fact]
    public async Task Two_Instances_Each_Send_The_Event_To_Their_Own_Connection_Once()
    {
        var first = StartInstance();
        var second = StartInstance();
        var user = await SignInAsync(first);
        using var onFirst = await HubSocket.ConnectAsync(first, user.Cookie, user.Http, negotiate: false);
        using var onSecond = await HubSocket.ConnectAsync(second, user.Cookie, second.CreateClient(), negotiate: false);
        await Task.Delay(2500, Token);

        await DeliverAsync(user.Account);

        Assert.NotNull(await onFirst.NextEventAsync("arrived", Long));
        Assert.NotNull(await onSecond.NextEventAsync("arrived", Long));
        // no instance forwards to the other (there is no backplane), so nobody gets the event twice
        Assert.Null(await onFirst.NextEventAsync("arrived", TimeSpan.FromSeconds(4)));
        Assert.Null(await onSecond.NextEventAsync("arrived", TimeSpan.FromSeconds(4)));
    }

    [Fact]
    public async Task Signing_Out_On_One_Instance_Closes_The_Channel_That_Is_Open_On_Another()
    {
        var first = StartInstance();
        var second = StartInstance(sessionCheck: TimeSpan.FromMilliseconds(300));
        var user = await SignInAsync(first);
        using var hub = await HubSocket.ConnectAsync(second, user.Cookie, second.CreateClient(), negotiate: false);

        var response = await CsrfClient.SendAsync(user.Http, HttpMethod.Post, "/api/v1/auth/logout", Token, null, user.Session);
        Assert.True(response.IsSuccessStatusCode);

        var closing = await hub.NextAsync(TimeSpan.FromSeconds(15));
        Assert.True(closing is null || closing.Value.GetProperty("type").GetInt32() == 7);
        Assert.Null(await hub.NextAsync(TimeSpan.FromSeconds(15)));
    }
}
