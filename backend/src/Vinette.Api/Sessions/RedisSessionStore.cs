using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Vinette.Api.Configuration;
using Vinette.Core.Sessions;

namespace Vinette.Api.Sessions;

/// <summary>
/// Session store in Redis, for several Vinette instances. A session is a hash under <c>{prefix}session:{id}</c>; the key
/// expires on its own after the longest possible session life, so a Redis restart or a forgotten session cannot pile up.
/// <c>{prefix}address:{mailbox}</c> is a set of the session ids of one mailbox, for "sign out everywhere".
/// The password is stored exactly as the instance gives it: already encrypted by <see cref="ICredentialProtector"/>.
/// </summary>
public sealed class RedisSessionStore(
    IConnectionMultiplexer redis,
    IOptions<RedisOptions> redisOptions,
    IOptions<SessionLifetimeOptions> lifetimeOptions) : ISessionStore
{
    private const string Address = "address";
    private const string Password = "password";
    private const string Created = "created";
    private const string Seen = "seen";
    private const string Remember = "remember";
    private const string ClientIp = "ip";

    // Moves "seen" forward only: concurrent requests of one session cannot move it back.
    private const string TouchScript = """
        if redis.call('EXISTS', KEYS[1]) == 0 then return 0 end
        local current = tonumber(redis.call('HGET', KEYS[1], 'seen'))
        if current == nil or tonumber(ARGV[1]) > current then redis.call('HSET', KEYS[1], 'seen', ARGV[1]) end
        return 1
        """;

    private readonly string prefix = redisOptions.Value.KeyPrefix;

    // The longest a session can live is the "stay signed in" period; the services enforce the exact limits.
    private readonly TimeSpan keyLifetime = lifetimeOptions.Value.RememberMeLifetime + TimeSpan.FromMinutes(5);

    private IDatabase Database => redis.GetDatabase();

    private string SessionKey(string id) => $"{prefix}session:{id}";

    private string AddressKey(string address) => $"{prefix}address:{address.ToUpperInvariant()}";

    public async Task AddAsync(Session session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();

        var key = SessionKey(session.Id);
        var addressKey = AddressKey(session.Address);
        var transaction = Database.CreateTransaction();
        _ = transaction.AddCondition(Condition.KeyNotExists(key));
        _ = transaction.HashSetAsync(key, ToEntries(session));
        _ = transaction.KeyExpireAsync(key, keyLifetime);
        _ = transaction.SetAddAsync(addressKey, session.Id);
        _ = transaction.KeyExpireAsync(addressKey, keyLifetime);

        if (!await transaction.ExecuteAsync().WaitAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("A session with this id already exists.");
        }
    }

    public async Task<Session?> GetAsync(string sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = await Database.HashGetAllAsync(SessionKey(sessionId)).WaitAsync(cancellationToken).ConfigureAwait(false);
        return FromEntries(sessionId, entries);
    }

    public async Task<bool> TouchAsync(string sessionId, DateTimeOffset seenAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await Database
            .ScriptEvaluateAsync(TouchScript, [(RedisKey)SessionKey(sessionId)], [(RedisValue)seenAt.ToUnixTimeMilliseconds()])
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        return (long)result == 1;
    }

    public async Task<bool> RemoveAsync(string sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = SessionKey(sessionId);
        var address = await Database.HashGetAsync(key, Address).WaitAsync(cancellationToken).ConfigureAwait(false);
        var removed = await Database.KeyDeleteAsync(key).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (address.HasValue)
        {
            await Database.SetRemoveAsync(AddressKey(address.ToString()), sessionId).WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return removed;
    }

    public async Task<int> RemoveWhereAsync(Func<Session, bool> predicate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var removed = 0;
        var database = Database;
        var sessionPrefix = $"{prefix}session:";
        foreach (var server in redis.GetServers().Where(s => !s.IsReplica && s.IsConnected))
        {
            await foreach (var key in server.KeysAsync(database.Database, $"{sessionPrefix}*").WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var id = key.ToString()[sessionPrefix.Length..];
                var session = FromEntries(id, await database.HashGetAllAsync(key).WaitAsync(cancellationToken).ConfigureAwait(false));
                if (session is not null && predicate(session) && await RemoveAsync(id, cancellationToken).ConfigureAwait(false))
                {
                    removed++;
                }
            }
        }

        return removed;
    }

    public async Task<int> RemoveAllForAddressAsync(string address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        cancellationToken.ThrowIfCancellationRequested();
        var addressKey = AddressKey(address);
        var ids = await Database.SetMembersAsync(addressKey).WaitAsync(cancellationToken).ConfigureAwait(false);
        var removed = 0;
        foreach (var id in ids)
        {
            if (await Database.KeyDeleteAsync(SessionKey(id.ToString())).WaitAsync(cancellationToken).ConfigureAwait(false))
            {
                removed++;
            }
        }

        await Database.KeyDeleteAsync(addressKey).WaitAsync(cancellationToken).ConfigureAwait(false);
        return removed;
    }

    private static HashEntry[] ToEntries(Session session)
    {
        var entries = new List<HashEntry>
        {
            new(Address, session.Address),
            new(Password, session.ProtectedPassword.ToArray()),
            new(Created, session.CreatedAt.ToUnixTimeMilliseconds()),
            new(Seen, session.LastSeenAt.ToUnixTimeMilliseconds()),
            new(Remember, session.RememberMe ? 1 : 0),
        };
        if (session.ClientIp is not null)
        {
            entries.Add(new HashEntry(ClientIp, session.ClientIp));
        }

        return [.. entries];
    }

    private static Session? FromEntries(string id, HashEntry[] entries)
    {
        if (entries.Length == 0)
        {
            return null;
        }

        var fields = entries.ToDictionary(e => e.Name.ToString(), e => e.Value, StringComparer.Ordinal);
        if (!fields.TryGetValue(Address, out var address)
            || !fields.TryGetValue(Password, out var password)
            || !fields.TryGetValue(Created, out var created)
            || !fields.TryGetValue(Seen, out var seen))
        {
            return null; // a half-written or foreign record is treated as absent
        }

        return new Session(
            id,
            address.ToString(),
            (byte[])password!,
            DateTimeOffset.FromUnixTimeMilliseconds((long)created),
            DateTimeOffset.FromUnixTimeMilliseconds((long)seen),
            fields.TryGetValue(Remember, out var remember) && (long)remember == 1,
            fields.TryGetValue(ClientIp, out var ip) ? ip.ToString() : null);
    }
}
