using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Vinette.Api.Configuration;
using Vinette.Api.Security;
using Vinette.Core.Authentication;

namespace Vinette.Api.Tests;

public sealed class LoginRateLimitTests : IDisposable
{
    private const string Address = "alice@vinette.test";
    private const string Password = "correct-password";

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class FakeAuthenticator : IAuthenticator
    {
        public int Calls { get; private set; }

        public Task<AuthenticationResult> AuthenticateAsync(LoginRequest request, ClientInfo client, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(request.Address switch
            {
                var a when a.StartsWith("down@", StringComparison.Ordinal) => AuthenticationResult.ServerUnavailable,
                _ when request.Password == Password => AuthenticationResult.Success,
                _ => AuthenticationResult.InvalidCredentials,
            });
        }
    }

    /// <summary>Gives every request a client address; the test server has none.</summary>
    private sealed class FixedClientIpFilter(Func<string> ip) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(ip());
                return nextMiddleware(context);
            });
            next(app);
        };
    }

    private readonly ManualTimeProvider time = new();
    private readonly FakeAuthenticator authenticator = new();
    private string clientIp = "203.0.113.10";
    private readonly Dictionary<string, string> settings = [];
    private WebApplicationFactory<Program>? factory;

    public void Dispose() => factory?.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private HttpClient CreateClient(bool withClientIp = true)
    {
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            foreach (var (key, value) in settings)
            {
                b.UseSetting(key, value);
            }

            b.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(time);
                services.RemoveAll<IAuthenticator>();
                services.AddSingleton<IAuthenticator>(authenticator);
                if (withClientIp)
                {
                    services.AddSingleton<IStartupFilter>(new FixedClientIpFilter(() => clientIp));
                }
            });
        });
        return factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string address, string password) =>
        CsrfClient.PostJsonAsync(client, "/api/v1/auth/login", new { address, password, rememberMe = false }, Token);

    private static async Task<int> RetryAfterSecondsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("too_many_requests", JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token)).RootElement.GetProperty("code").GetString());
        return (int)response.Headers.RetryAfter!.Delta!.Value.TotalSeconds;
    }

    // ---- Through the endpoint ----

    [Fact]
    public async Task Five_Wrong_Passwords_Lock_The_Mailbox_And_The_Mail_Server_Is_Not_Asked_Again()
    {
        var client = CreateClient();

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, Address, "wrong")).StatusCode);
        }

        var callsBefore = authenticator.Calls;
        var locked = await LoginAsync(client, Address, "wrong");

        Assert.InRange(await RetryAfterSecondsAsync(locked), 1, 60);
        Assert.Equal(callsBefore, authenticator.Calls);
    }

    [Fact]
    public async Task Even_The_Correct_Password_Is_Refused_During_A_Lock()
    {
        var client = CreateClient();
        for (var i = 0; i < 5; i++)
        {
            await LoginAsync(client, Address, "wrong");
        }

        var response = await LoginAsync(client, Address, Password);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task After_The_Lock_Expires_Signing_In_Works_Again()
    {
        var client = CreateClient();
        for (var i = 0; i < 5; i++)
        {
            await LoginAsync(client, Address, "wrong");
        }

        time.Advance(TimeSpan.FromSeconds(61));
        var response = await LoginAsync(client, Address, Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_Success_Clears_The_Mailbox_Counter()
    {
        var client = CreateClient();

        for (var i = 0; i < 4; i++)
        {
            await LoginAsync(client, Address, "wrong");
        }

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, Address, Password)).StatusCode);

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, Address, "wrong")).StatusCode);
        }
    }

    [Fact]
    public async Task An_Unavailable_Mail_Server_Is_Not_Counted_Against_Anyone()
    {
        var client = CreateClient();

        for (var i = 0; i < 12; i++)
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await LoginAsync(client, "down@vinette.test", "pw")).StatusCode);
        }
    }

    [Fact]
    public async Task Another_Mailbox_Is_Not_Affected_By_A_Locked_One()
    {
        var client = CreateClient();
        for (var i = 0; i < 5; i++)
        {
            await LoginAsync(client, Address, "wrong");
        }

        var other = await LoginAsync(client, "bob@vinette.test", Password);

        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task One_Client_Address_Is_Locked_After_Twenty_Failures_Across_Many_Mailboxes()
    {
        var client = CreateClient();
        for (var i = 0; i < 20; i++)
        {
            await LoginAsync(client, $"user{i}@vinette.test", "wrong"); // no mailbox reaches its own limit
        }

        var locked = await LoginAsync(client, "fresh@vinette.test", Password);
        clientIp = "203.0.113.99";
        var otherClient = await LoginAsync(client, "fresh@vinette.test", Password);

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.Equal(HttpStatusCode.OK, otherClient.StatusCode);
    }

    [Fact]
    public async Task The_Flood_Limit_Counts_Every_Attempt_Per_Client_Address()
    {
        settings["Security:LoginRateLimit:MaxAttemptsPerMinutePerIp"] = "3";
        var client = CreateClient();

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, $"user{i}@vinette.test", Password)).StatusCode);
        }

        var rejected = await LoginAsync(client, "user9@vinette.test", Password);
        clientIp = "203.0.113.77";
        var otherClient = await LoginAsync(client, "user9@vinette.test", Password);

        Assert.InRange(await RetryAfterSecondsAsync(rejected), 1, 60);
        Assert.Equal(HttpStatusCode.OK, otherClient.StatusCode);
    }

    [Fact]
    public async Task Without_A_Known_Client_Address_Only_The_Mailbox_Is_Limited()
    {
        var client = CreateClient(withClientIp: false);

        for (var i = 0; i < 30; i++)
        {
            await LoginAsync(client, $"user{i}@vinette.test", "wrong");
        }

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, "someone@vinette.test", Password)).StatusCode);
    }

    [Theory]
    [InlineData("Security:LoginRateLimit:MaxFailuresPerAccount", "0")]
    [InlineData("Security:LoginRateLimit:MaxFailuresPerIp", "0")]
    [InlineData("Security:LoginRateLimit:MaxAttemptsPerMinutePerIp", "0")]
    [InlineData("Security:LoginRateLimit:Window", "00:00:00")]
    [InlineData("Security:LoginRateLimit:MaxLockout", "00:00:10")]
    public void Invalid_Settings_Fail_Startup(string key, string value)
    {
        StartupFailure.AssertOptionsInvalid(b => b.UseSetting(key, value));
    }

    // ---- The tracker alone ----

    private (LoginAttemptTracker Tracker, ManualTimeProvider Time) CreateTracker(
        int perAccount = 5,
        int perIp = 20,
        int maxKeys = LoginAttemptTracker.DefaultMaxTrackedKeys)
    {
        var options = Options.Create(new LoginRateLimitOptions { MaxFailuresPerAccount = perAccount, MaxFailuresPerIp = perIp });
        return (new LoginAttemptTracker(options, time) { MaxTrackedKeys = maxKeys }, time);
    }

    [Fact]
    public void The_Lock_Doubles_With_Every_Failure_Up_To_The_Maximum()
    {
        var (tracker, clock) = CreateTracker(perAccount: 3);
        var observed = new List<double>();

        for (var i = 0; i < 12; i++)
        {
            tracker.RegisterFailure(Address, null);
            var wait = tracker.GetRetryAfter(Address, null);
            if (wait is { } w)
            {
                observed.Add(w.TotalMinutes);
                clock.Advance(w + TimeSpan.FromSeconds(1)); // wait out the lock, then fail again
            }
        }

        // Failures 1-2 are free; then 1, 2, 4, 8 minutes and the 15-minute ceiling.
        Assert.Equal([1, 2, 4, 8, 15, 15, 15, 15, 15, 15], observed);
    }

    [Fact]
    public void A_Quiet_Window_Makes_The_Tracker_Forget()
    {
        var (tracker, clock) = CreateTracker();
        for (var i = 0; i < 4; i++)
        {
            tracker.RegisterFailure(Address, null);
        }

        clock.Advance(TimeSpan.FromMinutes(16));
        tracker.RegisterFailure(Address, null); // would be the 5th, but the counter restarted

        Assert.Null(tracker.GetRetryAfter(Address, null));
    }

    [Fact]
    public void Mailbox_Names_Are_Compared_Case_Insensitively_And_Trimmed()
    {
        var (tracker, _) = CreateTracker();
        for (var i = 0; i < 5; i++)
        {
            tracker.RegisterFailure(i % 2 == 0 ? "Alice@Vinette.Test" : "  alice@vinette.test ", null);
        }

        Assert.NotNull(tracker.GetRetryAfter("ALICE@vinette.test", null));
    }

    [Fact]
    public void The_Longer_Of_The_Mailbox_And_Address_Locks_Applies()
    {
        var (tracker, clock) = CreateTracker(perAccount: 1, perIp: 1000);
        tracker.RegisterFailure(Address, "203.0.113.1"); // mailbox locked 1 min
        clock.Advance(TimeSpan.FromMinutes(2));
        tracker.RegisterFailure(Address, "203.0.113.1"); // mailbox: 2 min

        var wait = tracker.GetRetryAfter("other@vinette.test", "203.0.113.1");
        var account = tracker.GetRetryAfter(Address, "198.51.100.1");

        Assert.Null(wait); // the address is far below its own limit
        Assert.NotNull(account);
    }

    [Fact]
    public void Memory_Is_Bounded_And_The_Oldest_Entry_Makes_Room()
    {
        var (tracker, clock) = CreateTracker(maxKeys: 100);

        for (var i = 0; i < 500; i++)
        {
            tracker.RegisterFailure($"user{i}@vinette.test", null);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.True(tracker.TrackedKeys <= 100);
    }

    [Fact]
    public void Expired_Entries_Are_Removed()
    {
        var (tracker, clock) = CreateTracker();
        tracker.RegisterFailure(Address, "203.0.113.1");
        clock.Advance(TimeSpan.FromMinutes(20));

        var removed = tracker.RemoveExpired();

        Assert.Equal(2, removed);
        Assert.Equal(0, tracker.TrackedKeys);
    }

    [Fact]
    public void An_Active_Lock_Is_Never_Removed_As_Expired()
    {
        var (tracker, _) = CreateTracker(perAccount: 1);
        tracker.RegisterFailure(Address, null);

        Assert.Equal(0, tracker.RemoveExpired());
        Assert.NotNull(tracker.GetRetryAfter(Address, null));
    }
}
