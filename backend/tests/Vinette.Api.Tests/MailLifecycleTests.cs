using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vinette.Api.Configuration;
using Vinette.Api.Mail;
using Vinette.Core.Mail;
using Vinette.Mail;

namespace Vinette.Api.Tests;

/// <summary>Mail connections inside the running application: closed when idle and when the application stops.</summary>
public sealed class MailLifecycleTests
{
    private sealed class FakeSession : IMailSession
    {
        public bool IsConnected { get; private set; } = true;

        public bool Disposed { get; private set; }

        public Task PingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeFactory : IMailSessionFactory
    {
        public List<FakeSession> Sessions { get; } = [];

        public Task<IMailSession> OpenAsync(MailCredentials credentials, CancellationToken cancellationToken)
        {
            var session = new FakeSession();
            Sessions.Add(session);
            return Task.FromResult<IMailSession>(session);
        }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static (WebApplicationFactory<Program> App, FakeFactory Sessions) StartApp(string? idleTimeout = null)
    {
        var fake = new FakeFactory();
        var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            if (idleTimeout is not null)
            {
                b.UseSetting("Imap:ConnectionIdleTimeout", idleTimeout);
            }

            b.ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<IMailSessionFactory>(fake)));
        });
        return (app, fake);
    }

    private static Task<int> UseConnectionAsync(IMailGateway gateway, string session, string address) =>
        gateway.ExecuteAsync(
            new MailCredentials(session, address, "pw"),
            async (s, t) =>
            {
                await s.PingAsync(t);
                return 0;
            },
            Token);

    [Fact]
    public async Task Stopping_The_Application_Closes_Every_Mail_Connection()
    {
        var (app, fake) = StartApp();
        var gateway = app.Services.GetRequiredService<IMailGateway>();
        await UseConnectionAsync(gateway, "s1", "alice@vinette.test");
        await UseConnectionAsync(gateway, "s2", "bob@vinette.test");
        Assert.All(fake.Sessions, s => Assert.False(s.Disposed));

        await app.DisposeAsync();

        Assert.Equal(2, fake.Sessions.Count);
        Assert.All(fake.Sessions, s => Assert.True(s.Disposed));
    }

    [Fact]
    public async Task The_Cleanup_Service_Closes_Idle_Connections_After_The_Configured_Time()
    {
        var (app, fake) = StartApp(idleTimeout: "00:00:00.100");
        using var _ = app;
        var gateway = app.Services.GetRequiredService<IMailGateway>();
        await UseConnectionAsync(gateway, "s1", "alice@vinette.test");
        var cleanup = app.Services.GetServices<IHostedService>().OfType<MailConnectionCleanupService>().Single();

        await Task.Delay(300, Token);
        await cleanup.CloseIdleAsync(Token);

        Assert.True(fake.Sessions.Single().Disposed);
    }

    [Fact]
    public void The_Cleanup_Service_Looks_Often_Enough_But_Not_Too_Often()
    {
        var manager = new MailConnectionManager(new FakeFactory());
        MailConnectionCleanupService Create(TimeSpan idle) =>
            new(manager, Options.Create(new ImapOptions { ConnectionIdleTimeout = idle }), NullLogger<MailConnectionCleanupService>.Instance);

        Assert.Equal(TimeSpan.FromSeconds(30), Create(TimeSpan.FromMinutes(5)).Interval);
        Assert.Equal(TimeSpan.FromSeconds(5), Create(TimeSpan.FromSeconds(10)).Interval);
    }

    [Fact]
    public void A_Non_Positive_Idle_Timeout_Fails_Startup()
    {
        StartupFailure.AssertOptionsInvalid(b => b.UseSetting("Imap:ConnectionIdleTimeout", "00:00:00"));
    }
}
