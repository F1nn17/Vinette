using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using Vinette.Api.Errors;

namespace Vinette.Api.Security;

/// <summary>
/// Bound to the "Security:SendRateLimit" section (VIN-141): how much one mailbox may send. A stolen session, or a script that holds a valid one, would
/// otherwise turn the mail server into a sender of spam until the server itself stopped it. A limit of zero means no limit of that kind.
/// </summary>
public sealed class SendRateLimitOptions
{
    public const string SectionName = "Security:SendRateLimit";

    /// <summary>Messages one mailbox may send (or put off) in an hour. A message to several people is one message.</summary>
    [Range(0, 100_000)]
    public int MessagesPerHour { get; set; } = 60;

    /// <summary>Recipients (To, Cc and hidden, the same address once) one mailbox may write to in an hour.</summary>
    [Range(0, 1_000_000)]
    public int RecipientsPerHour { get; set; } = 200;

    /// <summary>Messages in a day (24 hours).</summary>
    [Range(0, 1_000_000)]
    public int MessagesPerDay { get; set; } = 300;

    /// <summary>Recipients in a day (24 hours).</summary>
    [Range(0, 10_000_000)]
    public int RecipientsPerDay { get; set; } = 600;
}

/// <summary>
/// Counts what each mailbox sends and says when it has to wait. A sliding log of the last 24 hours: a send is let through if the hour and the day both
/// stay within their limits, and is then written down; one that would exceed a limit is refused with the time after which it would fit. Counters live in
/// memory: with several instances the limits are per instance, and a restart starts them anew (the mail server's own limits are the other line).
/// </summary>
public sealed class SendRateLimiter(IOptions<SendRateLimitOptions> options, TimeProvider time)
{
    internal const int DefaultMaxTrackedMailboxes = 100_000;

    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);

    private readonly Lock gate = new();
    private readonly Dictionary<string, Queue<(DateTimeOffset At, int Recipients)>> log = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Upper bound of tracked mailboxes, so that endless new names cannot exhaust memory.</summary>
    internal int MaxTrackedMailboxes { get; init; } = DefaultMaxTrackedMailboxes;

    internal int TrackedMailboxes
    {
        get
        {
            lock (gate)
            {
                return log.Count;
            }
        }
    }

    /// <summary>
    /// Asks to send one message to <paramref name="recipients"/> people. Null: allowed, and written down. Otherwise how long the mailbox has to wait
    /// before the same send would be allowed (nothing is written down then).
    /// </summary>
    public TimeSpan? TryAcquire(string mailbox, int recipients)
    {
        ArgumentException.ThrowIfNullOrEmpty(mailbox);
        var limits = options.Value;
        if (limits is { MessagesPerHour: 0, RecipientsPerHour: 0, MessagesPerDay: 0, RecipientsPerDay: 0 })
        {
            return null;
        }

        var now = time.GetUtcNow();
        lock (gate)
        {
            if (!log.TryGetValue(mailbox, out var entries))
            {
                if (log.Count >= MaxTrackedMailboxes)
                {
                    Purge(now);
                }

                entries = new Queue<(DateTimeOffset, int)>();
                if (log.Count < MaxTrackedMailboxes)
                {
                    log[mailbox] = entries;
                }
            }

            while (entries.Count > 0 && entries.Peek().At <= now - Day)
            {
                _ = entries.Dequeue();
            }

            var wait = Wait(entries, now, Hour, limits.MessagesPerHour, limits.RecipientsPerHour, recipients);
            var day = Wait(entries, now, Day, limits.MessagesPerDay, limits.RecipientsPerDay, recipients);
            if (day > wait)
            {
                wait = day;
            }

            if (wait > TimeSpan.Zero)
            {
                return wait;
            }

            entries.Enqueue((now, recipients));
            return null;
        }
    }

    /// <summary>Forgets mailboxes whose last send is older than a day; run now and then so that quiet mailboxes do not stay in memory.</summary>
    public void Purge()
    {
        lock (gate)
        {
            Purge(time.GetUtcNow());
        }
    }

    // How long until one more message to `recipients` people fits in the window: the time at which enough of the oldest entries have left it.
    // Zero if it fits now. If it would not fit even into an empty window (more recipients than the limit allows at all), a whole window.
    private static TimeSpan Wait(Queue<(DateTimeOffset At, int Recipients)> entries, DateTimeOffset now, TimeSpan window, int maxMessages, int maxRecipients, int recipients)
    {
        var inWindow = entries.Where(e => e.At > now - window).ToList();
        var messages = inWindow.Count;
        var people = inWindow.Sum(e => e.Recipients);
        var wait = TimeSpan.Zero;
        var next = 0;
        while (Over(messages, people) && next < inWindow.Count)
        {
            wait = inWindow[next].At + window - now; // this entry has to leave the window first
            messages--;
            people -= inWindow[next].Recipients;
            next++;
        }

        return Over(messages, people) ? window : wait;

        bool Over(int sentMessages, int sentPeople) =>
            (maxMessages > 0 && sentMessages + 1 > maxMessages) || (maxRecipients > 0 && sentPeople + recipients > maxRecipients);
    }

    private void Purge(DateTimeOffset now)
    {
        foreach (var key in log.Where(e => e.Value.Count == 0 || e.Value.Peek().At <= now - Day).Select(e => e.Key).ToList())
        {
            _ = log.Remove(key);
        }

        // Entries inside a day old may still be there: queues are trimmed when their mailbox sends again.
        foreach (var (_, entries) in log)
        {
            while (entries.Count > 0 && entries.Peek().At <= now - Day)
            {
                _ = entries.Dequeue();
            }
        }
    }

    /// <summary>The refusal that goes to the user: 429, <c>Retry-After</c>, the code <c>send_limit_reached</c>.</summary>
    public static IResult Refusal(TimeSpan wait, HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
        http.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Results.Problem(
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "Too many messages were sent in a short time. Try again later.",
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.SendLimitReached });
    }
}

/// <summary>Drops the counters of mailboxes that have sent nothing for a day.</summary>
public sealed class SendRateLimitCleanupService(SendRateLimiter limiter) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                limiter.Purge();
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }
}
