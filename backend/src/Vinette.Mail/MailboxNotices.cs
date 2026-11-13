using MailKit;

namespace Vinette.Mail;

/// <summary>The marks a message has, as the server reported them.</summary>
/// <param name="Flags">The system marks (read, flag, replied to, ...).</param>
/// <param name="Keywords">The keywords (labels are among them).</param>
internal sealed record MessageMarks(MessageFlags Flags, IReadOnlyList<string> Keywords);

/// <summary>What the server reported since the last time, taken together.</summary>
/// <param name="CountChanged">The number of messages changed (mail may have arrived).</param>
/// <param name="Expunged">How many messages were removed.</param>
/// <param name="Marks">The latest marks of the messages the server named by UID; the last report about a message wins.</param>
/// <param name="MarksByPosition">The latest marks of the messages the server named by their place in the folder only (0-based); a position is good only while nothing was removed.</param>
/// <param name="Stale">What changed cannot be said (a reconnect to a server that cannot tell what happened meanwhile): the folder must be read again.</param>
internal sealed record NoticeBatch(bool CountChanged, int Expunged, IReadOnlyDictionary<uint, MessageMarks> Marks, IReadOnlyDictionary<int, MessageMarks> MarksByPosition, bool Stale = false)
{
    public bool IsEmpty => !CountChanged && Expunged == 0 && Marks.Count == 0 && MarksByPosition.Count == 0 && !Stale;
}

/// <summary>
/// Collects the notifications of the server while the connection waits (IDLE), so that a burst of them is answered once: several
/// reports about one message count as one, and an arrival followed by its removal is still seen as both. Thread-safe: the
/// notifications come on the thread that waits for the server.
/// </summary>
internal sealed class MailboxNotices
{
    private readonly Lock gate = new();
    private readonly Dictionary<uint, MessageMarks> marks = [];
    private readonly Dictionary<int, MessageMarks> marksByPosition = [];
    private bool countChanged;
    private int expunged;

    public void CountChanged()
    {
        lock (gate)
        {
            countChanged = true;
        }
    }

    public void Expunged(int count = 1)
    {
        lock (gate)
        {
            expunged += Math.Max(count, 0);
        }
    }

    public void Flags(uint? uid, int position, MessageFlags flags, IEnumerable<string> keywords)
    {
        lock (gate)
        {
            var reported = new MessageMarks(flags, [.. keywords]);
            if (uid is { } id)
            {
                marks[id] = reported;
            }
            else
            {
                marksByPosition[position] = reported;
            }
        }
    }

    public bool IsEmpty
    {
        get
        {
            lock (gate)
            {
                return !countChanged && expunged == 0 && marks.Count == 0 && marksByPosition.Count == 0;
            }
        }
    }

    /// <summary>What was reported so far; the collection starts again from nothing.</summary>
    public NoticeBatch Take()
    {
        lock (gate)
        {
            var batch = new NoticeBatch(countChanged, expunged, new Dictionary<uint, MessageMarks>(marks), new Dictionary<int, MessageMarks>(marksByPosition));
            countChanged = false;
            expunged = 0;
            marks.Clear();
            marksByPosition.Clear();
            return batch;
        }
    }
}
