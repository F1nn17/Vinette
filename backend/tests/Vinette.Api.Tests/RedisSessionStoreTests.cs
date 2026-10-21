using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Vinette.Api.Configuration;
using Vinette.Api.Sessions;
using Vinette.Core.Sessions;
using Vinette.TestSupport;

namespace Vinette.Api.Tests;

/// <summary>The Redis session store against a real Redis, alone and inside two application instances.</summary>
[Integration]
[Collection(RedisFixture.CollectionName)]
public sealed class RedisSessionStoreTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Blob = [1, 2, 3, 250, 251];

    private readonly RedisFixture redis;
    private readonly string prefix = $"test-{Guid.NewGuid():N}:";
    private ConnectionMultiplexer connection = null!;
    private RedisSessionStore store = null!;

    public RedisSessionStoreTests(RedisFixture redis) => this.redis = redis;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        store = NewStore(connection, prefix);
    }

    public async ValueTask DisposeAsync() => await connection.DisposeAsync();

    private static RedisSessionStore NewStore(IConnectionMultiplexer multiplexer, string keyPrefix) =>
        new(
            multiplexer,
            Options.Create(new RedisOptions { KeyPrefix = keyPrefix }),
            Options.Create(new SessionLifetimeOptions()));

    private static Session NewSession(string id, string address = "alice@vinette.test", DateTimeOffset? seen = null) =>
        new(id, address, Blob, Start, seen ?? Start, RememberMe: true, ClientIp: "203.0.113.5");

    [Fact]
    public async Task A_Stored_Session_Is_Read_Back_Unchanged()
    {
        await store.AddAsync(NewSession("s1"), Token);

        var session = await store.GetAsync("s1", Token);

        Assert.NotNull(session);
        Assert.Equal("alice@vinette.test", session.Address);
        Assert.Equal(Blob, session.ProtectedPassword.ToArray());
        Assert.Equal(Start, session.CreatedAt);
        Assert.Equal(Start, session.LastSeenAt);
        Assert.True(session.RememberMe);
        Assert.Equal("203.0.113.5", session.ClientIp);
    }

    [Fact]
    public async Task A_Session_Without_A_Client_Address_Is_Stored()
    {
        await store.AddAsync(NewSession("s1") with { ClientIp = null, RememberMe = false }, Token);

        var session = await store.GetAsync("s1", Token);

        Assert.NotNull(session);
        Assert.Null(session.ClientIp);
        Assert.False(session.RememberMe);
    }

    [Fact]
    public async Task An_Unknown_Session_Is_Not_Found()
    {
        Assert.Null(await store.GetAsync("nope", Token));
        Assert.False(await store.TouchAsync("nope", Start, Token));
        Assert.False(await store.RemoveAsync("nope", Token));
    }

    [Fact]
    public async Task Adding_The_Same_Id_Twice_Fails_And_Keeps_The_First()
    {
        await store.AddAsync(NewSession("s1"), Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AddAsync(NewSession("s1", "bob@vinette.test"), Token));

        Assert.Equal("alice@vinette.test", (await store.GetAsync("s1", Token))!.Address);
    }

    [Fact]
    public async Task Touch_Moves_The_Last_Seen_Time_Forward_Only()
    {
        await store.AddAsync(NewSession("s1"), Token);

        Assert.True(await store.TouchAsync("s1", Start.AddMinutes(10), Token));
        Assert.True(await store.TouchAsync("s1", Start.AddMinutes(5), Token)); // late request: exists, but no step back

        Assert.Equal(Start.AddMinutes(10), (await store.GetAsync("s1", Token))!.LastSeenAt);
    }

    [Fact]
    public async Task Remove_Deletes_The_Session()
    {
        await store.AddAsync(NewSession("s1"), Token);

        Assert.True(await store.RemoveAsync("s1", Token));

        Assert.Null(await store.GetAsync("s1", Token));
        Assert.False(await store.RemoveAsync("s1", Token));
    }

    [Fact]
    public async Task Sessions_Of_One_Mailbox_Are_Signed_Out_Together_Without_Touching_Others()
    {
        await store.AddAsync(NewSession("a1"), Token);
        await store.AddAsync(NewSession("a2", "ALICE@vinette.test"), Token); // the same mailbox, other letter case
        await store.AddAsync(NewSession("b1", "bob@vinette.test"), Token);

        var removed = await store.RemoveAllForAddressAsync("alice@vinette.test", Token);

        Assert.Equal(2, removed);
        Assert.Null(await store.GetAsync("a1", Token));
        Assert.Null(await store.GetAsync("a2", Token));
        Assert.NotNull(await store.GetAsync("b1", Token));
    }

    [Fact]
    public async Task Remove_Where_Removes_Only_What_The_Predicate_Selects()
    {
        await store.AddAsync(NewSession("old", seen: Start.AddHours(-5)), Token);
        await store.AddAsync(NewSession("fresh", "bob@vinette.test", seen: Start), Token);

        var removed = await store.RemoveWhereAsync(s => s.LastSeenAt < Start.AddHours(-1), Token);

        Assert.Equal(1, removed);
        Assert.Null(await store.GetAsync("old", Token));
        Assert.NotNull(await store.GetAsync("fresh", Token));
    }

    [Fact]
    public async Task Remove_Where_Ignores_Keys_Of_Other_Prefixes()
    {
        var other = NewStore(connection, $"other-{Guid.NewGuid():N}:");
        await other.AddAsync(NewSession("foreign"), Token);
        await store.AddAsync(NewSession("mine"), Token);

        var removed = await store.RemoveWhereAsync(_ => true, Token);

        Assert.Equal(1, removed);
        Assert.NotNull(await other.GetAsync("foreign", Token));
    }

    [Fact]
    public async Task Sessions_Expire_In_Redis_On_Their_Own()
    {
        await store.AddAsync(NewSession("s1"), Token);

        var ttl = await connection.GetDatabase().KeyTimeToLiveAsync($"{prefix}session:s1");

        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.FromDays(30), TimeSpan.FromDays(31));
    }

    [Fact]
    public async Task A_Session_Created_By_One_Instance_Is_Valid_On_Another()
    {
        const string settingStore = "Session:Store";
        void Configure(IWebHostBuilder b)
        {
            b.UseSetting(settingStore, "Redis");
            b.UseSetting("Redis:ConnectionString", redis.ConnectionString);
            b.UseSetting("Redis:KeyPrefix", prefix);
        }

        using var first = new WebApplicationFactory<Program>().WithWebHostBuilder(Configure);
        using var second = new WebApplicationFactory<Program>().WithWebHostBuilder(Configure);
        Assert.IsType<RedisSessionStore>(first.Services.GetRequiredService<ISessionStore>());

        var created = await first.Services.GetRequiredService<SessionService>().CreateAsync("alice@vinette.test", "s3cret", false, "203.0.113.5", Token);

        Assert.NotNull(created);
        var onSecond = second.Services.GetRequiredService<SessionService>();
        var seen = await onSecond.GetValidAsync(created.Id, Token);
        Assert.NotNull(seen);
        Assert.Equal("alice@vinette.test", seen.Address);
    }
}
