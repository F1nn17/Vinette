using Microsoft.Extensions.Options;
using Vinette.Api.Configuration;

namespace Vinette.Api.Security;

/// <summary>
/// Remembers wrong passwords per mailbox and per client address and locks them with a growing pause:
/// after the allowed number of failures the key is locked for 1 minute, every further failure doubles the lock
/// (1, 2, 4, 8 ... minutes) up to <see cref="LoginRateLimitOptions.MaxLockout"/>. A success clears the mailbox;
/// failures are forgotten after <see cref="LoginRateLimitOptions.Window"/> without a new failure or lock. Counters live in memory:
/// they are per instance and reset on restart.
/// </summary>
public sealed class LoginAttemptTracker(IOptions<LoginRateLimitOptions> options, TimeProvider time)
{
    internal const int DefaultMaxTrackedKeys = 100_000;

    private static readonly TimeSpan FirstLockout = TimeSpan.FromMinutes(1);

    private sealed class Entry
    {
        public int Failures { get; set; }

        public DateTimeOffset LastFailure { get; set; }

        public DateTimeOffset BlockedUntil { get; set; }
    }

    private readonly Lock gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    /// <summary>Upper bound of tracked keys, so that an attacker cannot exhaust memory with endless new names.</summary>
    internal int MaxTrackedKeys { get; init; } = DefaultMaxTrackedKeys;

    internal int TrackedKeys
    {
        get
        {
            lock (gate)
            {
                return entries.Count;
            }
        }
    }

    /// <summary>How long the caller must wait, or null when sign-in is allowed. The longer of the mailbox and address locks.</summary>
    public TimeSpan? GetRetryAfter(string address, string? clientIp)
    {
        var now = time.GetUtcNow();
        lock (gate)
        {
            var wait = TimeSpan.Zero;
            foreach (var key in Keys(address, clientIp))
            {
                if (entries.TryGetValue(key, out var entry) && entry.BlockedUntil > now)
                {
                    wait = TimeSpan.FromTicks(Math.Max(wait.Ticks, (entry.BlockedUntil - now).Ticks));
                }
            }

            return wait > TimeSpan.Zero ? wait : null;
        }
    }

    /// <summary>Records a wrong password. The caller's address may be unknown (null): then only the mailbox counts.</summary>
    public void RegisterFailure(string address, string? clientIp)
    {
        var settings = options.Value;
        var now = time.GetUtcNow();
        lock (gate)
        {
            Register(AccountKey(address), settings.MaxFailuresPerAccount, settings, now);
            if (clientIp is not null)
            {
                Register(IpKey(clientIp), settings.MaxFailuresPerIp, settings, now);
            }
        }
    }

    /// <summary>A correct password: the mailbox starts clean. The address counter is left to decay: it may serve many users.</summary>
    public void RegisterSuccess(string address)
    {
        lock (gate)
        {
            entries.Remove(AccountKey(address));
        }
    }

    /// <summary>Drops entries that no longer matter (called periodically).</summary>
    public int RemoveExpired()
    {
        var now = time.GetUtcNow();
        lock (gate)
        {
            return RemoveExpiredLocked(now);
        }
    }

    private void Register(string key, int allowedFailures, LoginRateLimitOptions settings, DateTimeOffset now)
    {
        if (!entries.TryGetValue(key, out var entry))
        {
            MakeRoom(now);
            entry = new Entry();
            entries[key] = entry;
        }

        if (now - Latest(entry.LastFailure, entry.BlockedUntil) > settings.Window)
        {
            entry.Failures = 0; // quiet for a whole window since the last failure or lock: start over
        }

        entry.Failures++;
        entry.LastFailure = now;

        if (entry.Failures >= allowedFailures)
        {
            var doublings = Math.Min(entry.Failures - allowedFailures, 20);
            var lockout = TimeSpan.FromTicks(Math.Min(FirstLockout.Ticks << doublings, settings.MaxLockout.Ticks));
            entry.BlockedUntil = now + lockout;
        }
    }

    private void MakeRoom(DateTimeOffset now)
    {
        if (entries.Count < MaxTrackedKeys)
        {
            return;
        }

        RemoveExpiredLocked(now);
        if (entries.Count >= MaxTrackedKeys)
        {
            var oldest = entries.MinBy(pair => pair.Value.LastFailure).Key;
            entries.Remove(oldest);
        }
    }

    private int RemoveExpiredLocked(DateTimeOffset now)
    {
        var window = options.Value.Window;
        var stale = entries.Where(pair => now - Latest(pair.Value.LastFailure, pair.Value.BlockedUntil) > window)
            .Select(pair => pair.Key)
            .ToList();
        foreach (var key in stale)
        {
            entries.Remove(key);
        }

        return stale.Count;
    }

    private static DateTimeOffset Latest(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static IEnumerable<string> Keys(string address, string? clientIp)
    {
        yield return AccountKey(address);
        if (clientIp is not null)
        {
            yield return IpKey(clientIp);
        }
    }

    private static string AccountKey(string address) => "a:" + address.Trim().ToLowerInvariant();

    private static string IpKey(string clientIp) => "i:" + clientIp;
}
