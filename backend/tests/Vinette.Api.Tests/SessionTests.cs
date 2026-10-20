using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Vinette.Api.Configuration;
using Vinette.Api.Sessions;
using Vinette.Core.Sessions;

namespace Vinette.Api.Tests;

public sealed class SessionTests
{
    private const string Address = "alice@vinette.test";
    private const string Password = "s3cret-pass";

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class Harness
    {
        public ManualTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));

        public InMemorySessionStore Store { get; } = new();

        public SessionOptions Options { get; } = new()
        {
            IdleTimeout = TimeSpan.FromHours(2),
            MaxLifetime = TimeSpan.FromDays(7),
            RememberMeLifetime = TimeSpan.FromDays(30),
        };

        public ICredentialProtector Protector { get; } =
            new DataProtectionCredentialProtector(new EphemeralDataProtectionProvider());

        public SessionService Service => new(Store, Protector, Microsoft.Extensions.Options.Options.Create(Options), Time);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- Credential protection ----

    [Fact]
    public void Password_Round_Trips_And_Is_Not_Stored_In_Clear()
    {
        var protector = new DataProtectionCredentialProtector(new EphemeralDataProtectionProvider());

        var protectedPassword = protector.Protect(Password);

        Assert.Equal(Password, protector.Unprotect(protectedPassword));
        Assert.False(protectedPassword.Span.IndexOf(Encoding.UTF8.GetBytes(Password)) >= 0);
    }

    [Fact]
    public void Password_Cannot_Be_Read_Without_The_Key()
    {
        var protectedPassword = new DataProtectionCredentialProtector(new EphemeralDataProtectionProvider()).Protect(Password);
        var otherKeys = new DataProtectionCredentialProtector(new EphemeralDataProtectionProvider());

        Assert.ThrowsAny<CryptographicException>(() => otherKeys.Unprotect(protectedPassword));
    }

    [Fact]
    public void Tampered_Data_Is_Rejected()
    {
        var protector = new DataProtectionCredentialProtector(new EphemeralDataProtectionProvider());
        var data = protector.Protect(Password).ToArray();
        data[^1] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(data));
    }

    [Fact]
    public void Persisted_Key_Ring_Survives_A_Restart_But_A_Different_One_Does_Not()
    {
        var keys = Directory.CreateTempSubdirectory("vinette-keys-");
        var otherKeys = Directory.CreateTempSubdirectory("vinette-keys-");
        try
        {
            var before = new DataProtectionCredentialProtector(DataProtectionProvider.Create(keys, b => b.SetApplicationName("Vinette")));
            var protectedPassword = before.Protect(Password);

            var afterRestart = new DataProtectionCredentialProtector(DataProtectionProvider.Create(keys, b => b.SetApplicationName("Vinette")));
            var otherRing = new DataProtectionCredentialProtector(DataProtectionProvider.Create(otherKeys, b => b.SetApplicationName("Vinette")));

            Assert.Equal(Password, afterRestart.Unprotect(protectedPassword));
            Assert.ThrowsAny<CryptographicException>(() => otherRing.Unprotect(protectedPassword));
        }
        finally
        {
            keys.Delete(recursive: true);
            otherKeys.Delete(recursive: true);
        }
    }

    [Fact]
    public void Payload_Is_Bound_To_The_Purpose()
    {
        var provider = new EphemeralDataProtectionProvider();
        var foreign = provider.CreateProtector("Some.Other.Purpose").Protect(Encoding.UTF8.GetBytes(Password));

        Assert.ThrowsAny<CryptographicException>(() => new DataProtectionCredentialProtector(provider).Unprotect(foreign));
    }

    // ---- Create / validate ----

    [Fact]
    public async Task Created_Session_Holds_The_Password_Only_In_Protected_Form()
    {
        var h = new Harness();

        var session = await h.Service.CreateAsync(Address, Password, false, "203.0.113.7", Token);

        Assert.True(SessionId.IsWellFormed(session.Id));
        Assert.Equal(Address, session.Address);
        Assert.False(session.ProtectedPassword.Span.IndexOf(Encoding.UTF8.GetBytes(Password)) >= 0);
        Assert.Equal(Password, h.Service.RevealPassword(session));
        Assert.NotNull(await h.Store.GetAsync(session.Id, Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-session-id")]
    public async Task Malformed_Or_Unknown_Ids_Give_No_Session(string? id)
    {
        var h = new Harness();

        Assert.Null(await h.Service.GetValidAsync(id, Token));
    }

    [Fact]
    public async Task Unknown_Well_Formed_Id_Gives_No_Session()
    {
        var h = new Harness();

        Assert.Null(await h.Service.GetValidAsync(SessionId.Create(), Token));
    }

    // ---- Timeouts ----

    [Fact]
    public async Task Session_Expires_After_The_Idle_Timeout()
    {
        var h = new Harness();
        var session = await h.Service.CreateAsync(Address, Password, false, null, Token);

        h.Time.Advance(TimeSpan.FromHours(2) + TimeSpan.FromSeconds(1));

        Assert.Null(await h.Service.GetValidAsync(session.Id, Token));
        Assert.Null(await h.Store.GetAsync(session.Id, Token)); // removed, not just ignored
    }

    [Fact]
    public async Task Activity_Keeps_The_Session_Alive_Within_The_Idle_Timeout()
    {
        var h = new Harness();
        var session = await h.Service.CreateAsync(Address, Password, false, null, Token);

        for (var i = 0; i < 5; i++)
        {
            h.Time.Advance(TimeSpan.FromHours(1.5));
            Assert.NotNull(await h.Service.GetValidAsync(session.Id, Token));
        }
    }

    [Fact]
    public async Task Session_Expires_After_The_Absolute_Lifetime_Even_With_Constant_Activity()
    {
        var h = new Harness();
        var session = await h.Service.CreateAsync(Address, Password, false, null, Token);

        // Active every hour for just under 7 days: still valid.
        for (var hour = 0; hour < 24 * 7 - 1; hour++)
        {
            h.Time.Advance(TimeSpan.FromHours(1));
            Assert.NotNull(await h.Service.GetValidAsync(session.Id, Token));
        }

        h.Time.Advance(TimeSpan.FromHours(2));

        Assert.Null(await h.Service.GetValidAsync(session.Id, Token));
    }

    [Fact]
    public async Task Remember_Me_Sessions_Survive_Long_Idle_Periods_But_Not_Their_Lifetime()
    {
        var h = new Harness();
        var session = await h.Service.CreateAsync(Address, Password, true, null, Token);

        h.Time.Advance(TimeSpan.FromDays(10)); // far beyond the 2 h idle timeout
        Assert.NotNull(await h.Service.GetValidAsync(session.Id, Token));

        h.Time.Advance(TimeSpan.FromDays(21)); // 31 days after login
        Assert.Null(await h.Service.GetValidAsync(session.Id, Token));
    }

    // ---- Ending sessions ----

    [Fact]
    public async Task Ended_Session_Is_Gone()
    {
        var h = new Harness();
        var session = await h.Service.CreateAsync(Address, Password, false, null, Token);

        await h.Service.EndAsync(session.Id, Token);

        Assert.Null(await h.Service.GetValidAsync(session.Id, Token));
    }

    [Fact]
    public async Task EndAll_Signs_A_Mailbox_Out_Everywhere_Case_Insensitively()
    {
        var h = new Harness();
        var first = await h.Service.CreateAsync(Address, Password, false, null, Token);
        var second = await h.Service.CreateAsync("ALICE@vinette.test", Password, true, null, Token);
        var other = await h.Service.CreateAsync("bob@vinette.test", "bob-pass", false, null, Token);

        var removed = await h.Service.EndAllAsync(Address, Token);

        Assert.Equal(2, removed);
        Assert.Null(await h.Service.GetValidAsync(first.Id, Token));
        Assert.Null(await h.Service.GetValidAsync(second.Id, Token));
        Assert.NotNull(await h.Service.GetValidAsync(other.Id, Token));
    }

    // ---- Cleanup ----

    [Fact]
    public async Task Cleanup_Removes_Only_Expired_Sessions()
    {
        var h = new Harness();
        var stale = await h.Service.CreateAsync(Address, Password, false, null, Token);
        h.Time.Advance(TimeSpan.FromHours(3));
        var fresh = await h.Service.CreateAsync("bob@vinette.test", "bob-pass", false, null, Token);
        var remembered = await h.Service.CreateAsync("carol@vinette.test", "pw", true, null, Token);

        var removed = await h.Service.RemoveExpiredAsync(Token);

        Assert.Equal(1, removed);
        Assert.Null(await h.Store.GetAsync(stale.Id, Token));
        Assert.NotNull(await h.Store.GetAsync(fresh.Id, Token));
        Assert.NotNull(await h.Store.GetAsync(remembered.Id, Token));
    }

    // ---- Store ----

    [Fact]
    public async Task Touch_Only_Moves_Activity_Forward()
    {
        var h = new Harness();
        var session = await h.Service.CreateAsync(Address, Password, false, null, Token);
        var later = session.LastSeenAt.AddMinutes(10);

        Assert.True(await h.Store.TouchAsync(session.Id, later, Token));
        Assert.True(await h.Store.TouchAsync(session.Id, session.LastSeenAt, Token)); // older value is ignored
        Assert.Equal(later, (await h.Store.GetAsync(session.Id, Token))!.LastSeenAt);
        Assert.False(await h.Store.TouchAsync("unknown", later, Token));
    }

    [Fact]
    public async Task Duplicate_Ids_Are_Rejected()
    {
        var h = new Harness();
        var session = await h.Service.CreateAsync(Address, Password, false, null, Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Store.AddAsync(session, Token));
    }

    [Fact]
    public async Task Store_Handles_Concurrent_Use()
    {
        var h = new Harness();
        var service = h.Service;

        var sessions = await Task.WhenAll(Enumerable.Range(0, 200)
            .Select(i => service.CreateAsync($"user{i}@vinette.test", "pw", false, null, Token)));
        await Task.WhenAll(sessions.Select(s => service.GetValidAsync(s.Id, Token)));
        await Task.WhenAll(sessions.Take(100).Select(s => service.EndAsync(s.Id, Token)));

        var remaining = await Task.WhenAll(sessions.Select(s => h.Store.GetAsync(s.Id, Token)));
        Assert.Equal(100, remaining.Count(s => s is not null));
    }
}
