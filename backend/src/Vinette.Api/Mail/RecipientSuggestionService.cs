using System.Collections.Concurrent;
using Vinette.Core.Mail;

namespace Vinette.Api.Mail;

/// <summary>
/// <see cref="IRecipientSuggestions"/>: the addresses are read from the newest mail of the "Sent" folder (the ones written to count most) and of
/// the inbox (the senders), once, and kept in memory per mailbox. Reading is bounded (the newest <see cref="ScanMessages"/> of each folder), so
/// a mailbox of fifty thousand messages costs the same as one of a thousand, and an answer is a search in memory. When what is kept gets old
/// (<see cref="Lifetime"/>) it is still used while a new one is read in the background, so that typing never waits for the mail server.
/// </summary>
public sealed partial class RecipientSuggestionService(IMailGateway gateway, ILogger<RecipientSuggestionService> logger, TimeProvider? timeProvider = null) : IRecipientSuggestions
{
    /// <summary>How many of the newest messages of each folder are read.</summary>
    public const int ScanMessages = 1000;

    /// <summary>How long what was read is trusted before it is read again (in the background).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>The most mailboxes whose addresses are kept; the least recently used go first.</summary>
    public const int MaxMailboxes = 2000;

    /// <summary>The most addresses kept for one mailbox (the best ones).</summary>
    public const int MaxContacts = 5000;

    /// <summary>How much a message the mailbox sent to somebody counts, against one it received from somebody (1).</summary>
    private const double SentWeight = 3;

    /// <summary>Mail this old counts half as much as mail from now.</summary>
    private const double HalfLifeDays = 90;

    /// <summary>The longest a read of the mailbox may take; it belongs to nobody's request, which may be gone.</summary>
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromSeconds(60);

    private static readonly char[] WordSeparators = [' ', '\t', '-', '.', ',', '_', '(', ')', '"', '\'', '<', '>', '@', '+'];

    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);

    private sealed record Contact(string Name, string Address, double Score, DateTimeOffset LastMail, string LowerAddress, string LowerName, string[] Words);

    private sealed class Index(DateTimeOffset builtAt, Contact[] contacts)
    {
        public DateTimeOffset BuiltAt { get; } = builtAt;

        public Contact[] Contacts { get; } = contacts;
    }

    private sealed class Entry
    {
        public object Gate { get; } = new();

        public Index? Current { get; set; }

        public Task<Index>? Building { get; set; }

        /// <summary>People remembered while the first read was still under way: put in when it ends, so that they are not lost.</summary>
        public List<MailAddressInfo> Pending { get; } = [];

        public long LastUsed { get; set; }
    }

    public async Task<IReadOnlyList<MailAddressInfo>> SuggestAsync(MailCredentials credentials, string query, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        count = Math.Min(count, SuggestionLimits.MaxCount);
        var text = (query ?? string.Empty).Trim();
        if (text.Length > SuggestionLimits.MaxQueryLength)
        {
            text = text[..SuggestionLimits.MaxQueryLength];
        }

        var mailbox = credentials.Address.ToLowerInvariant();
        var index = await IndexOfAsync(credentials, mailbox, cancellationToken).ConfigureAwait(false);
        return Match(index, mailbox, text, count);
    }

    public async Task<IReadOnlyList<KnownRecipient>> LookupAsync(MailCredentials credentials, IReadOnlyList<string> addresses, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(addresses);
        var mailbox = credentials.Address.ToLowerInvariant();
        var index = await IndexOfAsync(credentials, mailbox, cancellationToken).ConfigureAwait(false);
        var known = index.Contacts.ToDictionary(c => c.LowerAddress, c => c.LastMail, StringComparer.Ordinal);
        return [.. addresses.Select(a => new KnownRecipient(a, known.TryGetValue(a.Trim().ToLowerInvariant(), out var last) ? last : null))];
    }

    /// <summary>What is known of the mailbox's correspondents: from memory, read first if there is nothing yet, refreshed in the background when old.</summary>
    private async Task<Index> IndexOfAsync(MailCredentials credentials, string mailbox, CancellationToken cancellationToken)
    {
        var entry = Touch(mailbox);
        Index? index;
        Task<Index>? waiting = null;
        lock (entry.Gate)
        {
            index = entry.Current;
            if (entry.Building is { IsCompleted: true })
            {
                entry.Building = null; // a read that has ended (and failed, or the continuation has not run yet) is not waited for
            }

            if (index is null)
            {
                waiting = entry.Building ??= BuildAsync(entry, credentials);
            }
            else if (time.GetUtcNow() - index.BuiltAt > Lifetime && entry.Building is null)
            {
                _ = RefreshAsync(entry, credentials); // the old one answers meanwhile
            }
        }

        // The read belongs to the mailbox, not to this request: a request that is given up on stops waiting, the read goes on for the next one.
        return index ?? await waiting!.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Remember(string mailbox, IEnumerable<MailAddressInfo> recipients)
    {
        ArgumentException.ThrowIfNullOrEmpty(mailbox);
        ArgumentNullException.ThrowIfNull(recipients);
        var key = mailbox.ToLowerInvariant();
        if (!entries.TryGetValue(key, out var entry))
        {
            return; // nothing is kept for this mailbox yet; the first read will find the sent message in the "Sent" folder
        }

        lock (entry.Gate)
        {
            if (entry.Current is { } current)
            {
                entry.Current = Merge(current, recipients, key, time.GetUtcNow());
            }
            else if (entry.Building is not null)
            {
                entry.Pending.AddRange(recipients); // the read that is under way may have started before this message went out
            }
        }
    }

    /// <summary>The index with the people taken in as if they had just been written to.</summary>
    private static Index Merge(Index current, IEnumerable<MailAddressInfo> recipients, string ownAddress, DateTimeOffset now)
    {
        var merged = current.Contacts.ToDictionary(c => c.LowerAddress, StringComparer.Ordinal);
        foreach (var person in recipients)
        {
            var address = (person.Address ?? string.Empty).Trim();
            var lower = address.ToLowerInvariant();
            if (!IsAddress(address) || lower == ownAddress)
            {
                continue;
            }

            merged[lower] = merged.TryGetValue(lower, out var known)
                ? Make(string.IsNullOrWhiteSpace(person.Name) ? known.Name : person.Name.Trim(), known.Address, known.Score + SentWeight, now)
                : Make(person.Name?.Trim() ?? string.Empty, address, SentWeight, now);
        }

        return new Index(current.BuiltAt, [.. merged.Values.OrderByDescending(c => c.Score).Take(MaxContacts)]);
    }

    private Entry Touch(string mailbox)
    {
        var entry = entries.GetOrAdd(mailbox, _ => new Entry());
        entry.LastUsed = time.GetTimestamp();
        if (entries.Count > MaxMailboxes)
        {
            foreach (var old in entries.OrderBy(e => e.Value.LastUsed).Take(entries.Count - MaxMailboxes + (MaxMailboxes / 10)).Select(e => e.Key))
            {
                _ = entries.TryRemove(old, out _);
            }
        }

        return entry;
    }

    private Task<Index> BuildAsync(Entry entry, MailCredentials credentials)
    {
        var own = credentials.Address.ToLowerInvariant();
        // Runs on a thread of its own: the caller holds the lock while it starts this, and the read must not run under it.
        return Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(BuildTimeout);
                var contacts = await ReadAsync(credentials, timeout.Token).ConfigureAwait(false);
                var built = new Index(time.GetUtcNow(), contacts);
                lock (entry.Gate)
                {
                    // What was sent while the mailbox was being read is taken in, and the index is in place before anyone is told it is ready.
                    if (entry.Pending.Count > 0)
                    {
                        built = Merge(built, entry.Pending, own, time.GetUtcNow());
                        entry.Pending.Clear();
                    }

                    entry.Current = built;
                    entry.Building = null;
                }

                return built;
            }
            catch
            {
                lock (entry.Gate)
                {
                    entry.Building = null; // a failed read is tried again by the next request
                    entry.Pending.Clear();
                }

                throw;
            }
        });
    }

    private async Task RefreshAsync(Entry entry, MailCredentials credentials)
    {
        try
        {
            entry.Building = BuildAsync(entry, credentials);
            _ = await entry.Building.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is MailUnavailableException or MailBusyException or MailAccessRevokedException or OperationCanceledException)
        {
            RefreshFailed(logger, exception.GetType().Name); // the old addresses stay in use
        }
    }

    private async Task<Contact[]> ReadAsync(MailCredentials credentials, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var own = credentials.Address.ToLowerInvariant();
        return await gateway.ExecuteAsync(
            credentials,
            async (session, token) =>
            {
                var folders = FolderRoleGuesser.Complete(await session.GetFoldersAsync(token).ConfigureAwait(false)); // the same roles as the sidebar shows
                var found = new Dictionary<string, Tally>(StringComparer.Ordinal);
                foreach (var (role, weight) in new[] { (FolderRole.Sent, SentWeight), (FolderRole.Inbox, 1d) })
                {
                    var folder = folders.FirstOrDefault(f => f.Role == role && f.CanContainMessages);
                    if (folder is null)
                    {
                        continue;
                    }

                    await ScanAsync(session, folder.Path, role == FolderRole.Sent, weight, own, now, found, token).ConfigureAwait(false);
                }

                return found.Values
                    .OrderByDescending(t => t.Score)
                    .Take(MaxContacts)
                    .Select(t => Make(t.Name, t.Address, t.Score, t.LastMail))
                    .ToArray();
            },
            cancellationToken).ConfigureAwait(false);
    }

    private sealed class Tally(string address)
    {
        public string Address { get; } = address;

        public string Name { get; set; } = string.Empty;

        public DateTimeOffset NameDate { get; set; } = DateTimeOffset.MinValue;

        public double Score { get; set; }

        /// <summary>The date of the newest message the address was found in.</summary>
        public DateTimeOffset LastMail { get; set; } = DateTimeOffset.MinValue;
    }

    private static async Task ScanAsync(IMailSession session, string folder, bool recipients, double weight, string own, DateTimeOffset now, Dictionary<string, Tally> found, CancellationToken cancellationToken)
    {
        MessageCursor? after = null;
        var scanned = 0;
        while (scanned < ScanMessages)
        {
            MessagePage page;
            try
            {
                page = await session.ListMessagesAsync(new MessageListQuery(folder, after, Math.Min(MessageListQuery.MaxLimit, ScanMessages - scanned), NewestFirst: true, IncludePreview: false), cancellationToken).ConfigureAwait(false);
            }
            catch (MailFolderNotFoundException)
            {
                return; // a folder that cannot be read is no source
            }

            foreach (var message in page.Items)
            {
                var decay = Math.Pow(0.5, Math.Max(0, (now - message.Date).TotalDays) / HalfLifeDays);
                var people = recipients ? message.To : message.From is { } from ? [from] : [];
                foreach (var person in people)
                {
                    var address = (person.Address ?? string.Empty).Trim();
                    var lower = address.ToLowerInvariant();
                    if (!IsAddress(address) || lower == own)
                    {
                        continue;
                    }

                    if (!found.TryGetValue(lower, out var tally))
                    {
                        found[lower] = tally = new Tally(address);
                    }

                    tally.Score += weight * decay;
                    if (message.Date > tally.LastMail)
                    {
                        tally.LastMail = message.Date;
                    }

                    if (!string.IsNullOrWhiteSpace(person.Name) && message.Date >= tally.NameDate)
                    {
                        tally.Name = person.Name.Trim(); // the name the person was last seen under
                        tally.NameDate = message.Date;
                    }
                }
            }

            scanned += page.Items.Count;
            if (page.Next is not { } next || page.Items.Count == 0)
            {
                return;
            }

            after = next;
        }
    }

    private static bool IsAddress(string address) =>
        address.Length is > 3 and <= 254 && address.Count(c => c == '@') == 1 && !address.StartsWith('@') && !address.EndsWith('@') && !address.Any(c => char.IsControl(c) || char.IsWhiteSpace(c));

    private static Contact Make(string name, string address, double score, DateTimeOffset lastMail)
    {
        var lowerName = name.ToLowerInvariant();
        return new Contact(name, address, score, lastMail, address.ToLowerInvariant(), lowerName, lowerName.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>0: the text starts the address or the name; 1: it starts words of the name (or every word typed starts some word); 2: it is somewhere inside; -1: no match.</summary>
    private static int Rank(Contact contact, string query, string[] tokens)
    {
        if (query.Length == 0)
        {
            return 0;
        }

        if (contact.LowerAddress.StartsWith(query, StringComparison.Ordinal) || contact.LowerName.StartsWith(query, StringComparison.Ordinal))
        {
            return 0;
        }

        if (tokens.All(t => contact.Words.Any(w => w.StartsWith(t, StringComparison.Ordinal)) || contact.LowerAddress.StartsWith(t, StringComparison.Ordinal)))
        {
            return 1;
        }

        return contact.LowerAddress.Contains(query, StringComparison.Ordinal) || contact.LowerName.Contains(query, StringComparison.Ordinal) ? 2 : -1;
    }

    private static List<MailAddressInfo> Match(Index index, string own, string query, int count)
    {
        var lower = query.ToLowerInvariant();
        var tokens = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return index.Contacts
            .Where(c => c.LowerAddress != own)
            .Select(c => (Contact: c, Rank: Rank(c, lower, tokens)))
            .Where(m => m.Rank >= 0)
            .OrderBy(m => m.Rank)
            .ThenByDescending(m => m.Contact.Score)
            .ThenBy(m => m.Contact.LowerAddress, StringComparer.Ordinal)
            .Take(count)
            .Select(m => new MailAddressInfo(m.Contact.Name, m.Contact.Address))
            .ToList();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The addresses for suggestions could not be refreshed ({Reason}); the earlier ones stay in use")]
    private static partial void RefreshFailed(ILogger logger, string reason);
}
