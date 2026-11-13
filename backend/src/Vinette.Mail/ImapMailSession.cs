using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Vinette.Core.Mail;

namespace Vinette.Mail;

/// <summary>
/// One authenticated IMAP connection. Not thread-safe by design: the connection manager lets only one command run at a time.
/// </summary>
internal sealed class ImapMailSession(ImapClient client, bool previewExtension = true, long maxViewableBytes = MessageLimits.MaxViewableBytes) : IMailSession
{
    static ImapMailSession() => LegacyCharsets.Ensure();

    private bool PreviewExtension => previewExtension;

    /// <summary>Tests only: false moves messages by COPY, mark and expunge even if the server has MOVE.</summary>
    internal bool MoveExtension { get; init; } = true;

    public bool IsConnected => client.IsConnected && client.IsAuthenticated;

    public Task PingAsync(CancellationToken cancellationToken) => client.NoOpAsync(cancellationToken);

    public async Task<IReadOnlyList<MailFolderInfo>> GetFoldersAsync(CancellationToken cancellationToken)
    {
        // One LIST-STATUS round trip where the server has it; MailKit falls back to a STATUS per folder otherwise.
        var root = client.PersonalNamespaces.Count > 0
            ? client.PersonalNamespaces[0]
            : new FolderNamespace('/', string.Empty);
        var listed = await client.GetFoldersAsync(
            root,
            StatusItems.Count | StatusItems.Unread,
            subscribedOnly: false,
            cancellationToken).ConfigureAwait(false);
        var subscribed = (await client.GetFoldersAsync(root, StatusItems.None, subscribedOnly: true, cancellationToken).ConfigureAwait(false))
            .Select(f => f.FullName)
            .ToHashSet(StringComparer.Ordinal);

        var folders = new List<MailFolderInfo>(listed.Count + 1);
        var inbox = client.Inbox;
        if (listed.All(f => !IsInbox(f)))
        {
            // Some servers list the personal namespace without INBOX, which lives next to it.
            await inbox.StatusAsync(StatusItems.Count | StatusItems.Unread, cancellationToken).ConfigureAwait(false);
            folders.Add(Map(inbox, subscribed));
        }

        foreach (var folder in listed)
        {
            if (folder.Attributes.HasFlag(FolderAttributes.NonExistent))
            {
                continue;
            }

            folders.Add(Map(folder, subscribed));
        }

        return folders;
    }

    public async Task<MessagePage> ListMessagesAsync(MessageListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var (folder, uidValidity) = await OpenAsync(query.Folder, cancellationToken).ConfigureAwait(false);
        if (query.After is { } after && after.UidValidity != uidValidity)
        {
            throw new UidValidityChangedException(query.Folder, after.UidValidity, uidValidity);
        }

        if (query.Filter != MessageFilter.All)
        {
            return await ListFilteredAsync(folder, query, uidValidity, cancellationToken).ConfigureAwait(false);
        }

        var total = folder.Count;

        // The messages are in UID order, so the position of a UID among them is the number of messages up to it. Counting
        // them is one cheap search; with it the page is a plain range of sequence numbers, however large the folder is.
        var before = query.After is { } c ? await CountUpToAsync(folder, c.Uid, query.NewestFirst, cancellationToken).ConfigureAwait(false) : (int?)null;
        int first;
        int last; // 0-based, inclusive
        if (query.NewestFirst)
        {
            var end = before ?? total; // messages older than the cursor are 1..end
            last = end - 1;
            first = Math.Max(0, end - query.Limit);
        }
        else
        {
            first = before ?? 0; // messages up to the cursor are 1..before
            last = Math.Min(total, first + query.Limit) - 1;
        }

        var items = new List<MessageListItem>();
        if (last >= first && total > 0)
        {
            var wanted = query.IncludePreview ? SummaryItems | MessageSummaryItems.PreviewText : SummaryItems;
            if (!PreviewExtension)
            {
                client.Capabilities.Disable(ImapCapability.Preview); // test switch: use the fallback that reads the beginning of the body
            }

            var fetched = await folder.FetchAsync(first, last, wanted, cancellationToken).ConfigureAwait(false);
            IEnumerable<IMessageSummary> ordered = query.NewestFirst ? fetched.OrderByDescending(m => m.UniqueId.Id) : fetched.OrderBy(m => m.UniqueId.Id);

            // Mail may have been deleted between the count and the fetch: only what is really on the right side of the cursor.
            items.AddRange(ordered
                .Where(m => query.After is not { } a || (query.NewestFirst ? m.UniqueId.Id < a.Uid : m.UniqueId.Id > a.Uid))
                .Select(m => ToSummary(query.Folder, uidValidity, m)));
        }

        var hasMore = query.NewestFirst ? first > 0 : last + 1 < total;
        MessageCursor? next = hasMore && items.Count > 0 ? new MessageCursor(uidValidity, items[^1].Id.Uid) : null;
        return new MessagePage(uidValidity, items, next, total);
    }

    /// <summary>
    /// A filtered list: the server searches for the matching UIDs (one command, the answer is just numbers), the page is
    /// cut out of them by the cursor's UID, and only those messages are fetched. Deleted or newly arrived mail cannot shift
    /// a page, because the position is a UID and never an index.
    /// </summary>
    private async Task<MessagePage> ListFilteredAsync(IMailFolder folder, MessageListQuery query, uint uidValidity, CancellationToken cancellationToken)
    {
        var search = query.Filter == MessageFilter.Unread ? SearchQuery.NotSeen : SearchQuery.Flagged;
        var matching = (await folder.SearchAsync(search, cancellationToken).ConfigureAwait(false)).Select(u => u.Id).Order().ToList();

        IEnumerable<uint> candidates = matching;
        if (query.After is { } after)
        {
            candidates = query.NewestFirst ? matching.Where(u => u < after.Uid) : matching.Where(u => u > after.Uid);
        }

        var ordered = query.NewestFirst ? candidates.Reverse() : candidates;
        var left = ordered.ToList();
        var pageUids = left.Take(query.Limit).ToList();
        var items = new List<MessageListItem>(pageUids.Count);
        if (pageUids.Count > 0)
        {
            var wanted = query.IncludePreview ? SummaryItems | MessageSummaryItems.PreviewText : SummaryItems;
            if (!PreviewExtension)
            {
                client.Capabilities.Disable(ImapCapability.Preview);
            }

            var fetched = await folder.FetchAsync([.. pageUids.Select(u => new UniqueId(u))], wanted, cancellationToken).ConfigureAwait(false);
            IEnumerable<IMessageSummary> inOrder = query.NewestFirst ? fetched.OrderByDescending(m => m.UniqueId.Id) : fetched.OrderBy(m => m.UniqueId.Id);
            items.AddRange(inOrder.Select(m => ToSummary(query.Folder, uidValidity, m)));
        }

        MessageCursor? next = left.Count > pageUids.Count && items.Count > 0 ? new MessageCursor(uidValidity, items[^1].Id.Uid) : null;
        return new MessagePage(uidValidity, items, next, matching.Count);
    }

    /// <summary>Opens a folder read-only (nothing is marked as read by looking); a folder that is missing or is a container is "not found".</summary>
    private async Task<(IMailFolder Folder, uint UidValidity)> OpenAsync(string path, CancellationToken cancellationToken, FolderAccess access = FolderAccess.ReadOnly)
    {
        try
        {
            var folder = IsInboxPath(path) ? client.Inbox : await client.GetFolderAsync(path, cancellationToken).ConfigureAwait(false);
            _ = await folder.OpenAsync(access, cancellationToken).ConfigureAwait(false);
            return (folder, (uint)folder.UidValidity);
        }
        catch (Exception exception) when (exception is FolderNotFoundException or ImapCommandException or InvalidOperationException)
        {
            // A missing folder and a folder that cannot be opened (a container) look the same to the caller.
            throw new MailFolderNotFoundException("The folder does not exist or cannot hold messages.", exception);
        }
    }

    public async Task<MessageFlagState> UpdateFlagsAsync(MessageId id, MessageFlagChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        var (folder, uidValidity) = await OpenAsync(id.Folder, cancellationToken, FolderAccess.ReadWrite).ConfigureAwait(false);
        if (id.UidValidity != uidValidity)
        {
            throw new UidValidityChangedException(id.Folder, id.UidValidity, uidValidity);
        }

        var uid = new UniqueId(id.Uid);
        var before = await FlagsOfAsync(folder, uid, cancellationToken).ConfigureAwait(false) ?? throw new MailMessageNotFoundException("The message is not in the folder.");
        await StoreAsync(folder, uid, MessageFlags.Seen, change.Seen, cancellationToken).ConfigureAwait(false);
        await StoreAsync(folder, uid, MessageFlags.Flagged, change.Flagged, cancellationToken).ConfigureAwait(false);

        var after = change.IsEmpty ? before : await FlagsOfAsync(folder, uid, cancellationToken).ConfigureAwait(false) ?? throw new MailMessageNotFoundException("The message is not in the folder.");
        return new MessageFlagState(
            after.HasFlag(MessageFlags.Seen),
            after.HasFlag(MessageFlags.Flagged),
            after.HasFlag(MessageFlags.Answered),
            after.HasFlag(MessageFlags.Draft));
    }

    public async Task<MessageId?> MoveMessageAsync(MessageId id, string targetFolder, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetFolder);
        var (source, uidValidity) = await OpenAsync(id.Folder, cancellationToken, FolderAccess.ReadWrite).ConfigureAwait(false);
        if (id.UidValidity != uidValidity)
        {
            throw new UidValidityChangedException(id.Folder, id.UidValidity, uidValidity);
        }

        IMailFolder destination;
        try
        {
            destination = IsInboxPath(targetFolder) ? client.Inbox : await client.GetFolderAsync(targetFolder, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FolderNotFoundException or ImapCommandException or InvalidOperationException)
        {
            throw new MailFolderNotFoundException("The folder does not exist or cannot hold messages.", exception);
        }

        var uid = new UniqueId(id.Uid);
        _ = await FlagsOfAsync(source, uid, cancellationToken).ConfigureAwait(false) ?? throw new MailMessageNotFoundException("The message is not in the folder.");

        UniqueId? moved;
        try
        {
            if (MoveExtension && client.Capabilities.Contains(ImapCapability.Move))
            {
                moved = await source.MoveToAsync(uid, destination, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // The copy comes first: if it fails nothing was changed. Only the one message is removed, never others that happen to be marked.
                moved = await source.CopyToAsync(uid, destination, cancellationToken).ConfigureAwait(false);
                await source.AddFlagsAsync(uid, MessageFlags.Deleted, silent: true, cancellationToken).ConfigureAwait(false);
                await source.ExpungeAsync([uid], cancellationToken).ConfigureAwait(false);
            }
        }
        catch (FolderNotFoundException exception)
        {
            throw new MailFolderNotFoundException("The folder does not exist or cannot hold messages.", exception);
        }

        return moved is { Validity: > 0 } where ? new MessageId(targetFolder, where.Validity, where.Id) : null;
    }

    public async Task<IReadOnlyList<MovedMessage>> MoveMessagesAsync(IReadOnlyList<MessageId> ids, string targetFolder, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetFolder);
        var (source, present) = await OpenBatchAsync(ids, cancellationToken).ConfigureAwait(false);
        if (present.Count == 0)
        {
            return [];
        }

        IMailFolder destination;
        try
        {
            destination = IsInboxPath(targetFolder) ? client.Inbox : await client.GetFolderAsync(targetFolder, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FolderNotFoundException or ImapCommandException or InvalidOperationException)
        {
            throw new MailFolderNotFoundException("The folder does not exist or cannot hold messages.", exception);
        }

        UniqueIdMap map;
        try
        {
            if (MoveExtension && client.Capabilities.Contains(ImapCapability.Move))
            {
                map = await source.MoveToAsync(present, destination, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // As for one message: the copy first, then only these messages are marked and removed.
                map = await source.CopyToAsync(present, destination, cancellationToken).ConfigureAwait(false);
                await source.AddFlagsAsync(present, MessageFlags.Deleted, silent: true, cancellationToken).ConfigureAwait(false);
                await source.ExpungeAsync(present, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (FolderNotFoundException exception)
        {
            throw new MailFolderNotFoundException("The folder does not exist or cannot hold messages.", exception);
        }

        var folder = ids[0].Folder;
        var validity = ids[0].UidValidity;
        return [.. present.Select(uid => new MovedMessage(
            new MessageId(folder, validity, uid.Id),
            map.TryGetValue(uid, out var moved) && moved.Validity > 0 ? new MessageId(targetFolder, moved.Validity, moved.Id) : null))];
    }

    public async Task<IReadOnlyList<MessageId>> UpdateFlagsOfManyAsync(IReadOnlyList<MessageId> ids, MessageFlagChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        var (folder, present) = await OpenBatchAsync(ids, cancellationToken).ConfigureAwait(false);
        if (present.Count > 0)
        {
            await StoreManyAsync(folder, present, MessageFlags.Seen, change.Seen, cancellationToken).ConfigureAwait(false);
            await StoreManyAsync(folder, present, MessageFlags.Flagged, change.Flagged, cancellationToken).ConfigureAwait(false);
        }

        return [.. present.Select(uid => new MessageId(ids[0].Folder, ids[0].UidValidity, uid.Id))];
    }

    /// <summary>Opens the folder of a batch for writing and finds which of its messages are still there (one search).</summary>
    private async Task<(IMailFolder Folder, IList<UniqueId> Present)> OpenBatchAsync(IReadOnlyList<MessageId> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (!MessageBatch.IsValid(ids, out var problem))
        {
            throw new ArgumentException(problem, nameof(ids));
        }

        var (folder, uidValidity) = await OpenAsync(ids[0].Folder, cancellationToken, FolderAccess.ReadWrite).ConfigureAwait(false);
        if (ids[0].UidValidity != uidValidity)
        {
            throw new UidValidityChangedException(ids[0].Folder, ids[0].UidValidity, uidValidity);
        }

        var wanted = ids.Select(i => new UniqueId(i.Uid)).ToList();
        var present = await folder.SearchAsync(SearchQuery.Uids(wanted), cancellationToken).ConfigureAwait(false);
        return (folder, present);
    }

    private static async Task StoreManyAsync(IMailFolder folder, IList<UniqueId> uids, MessageFlags flag, bool? wanted, CancellationToken cancellationToken)
    {
        if (wanted is true)
        {
            await folder.AddFlagsAsync(uids, flag, silent: true, cancellationToken).ConfigureAwait(false);
        }
        else if (wanted is false)
        {
            await folder.RemoveFlagsAsync(uids, flag, silent: true, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DeleteMessagePermanentlyAsync(MessageId id, CancellationToken cancellationToken)
    {
        var (folder, uidValidity) = await OpenAsync(id.Folder, cancellationToken, FolderAccess.ReadWrite).ConfigureAwait(false);
        if (id.UidValidity != uidValidity)
        {
            throw new UidValidityChangedException(id.Folder, id.UidValidity, uidValidity);
        }

        var uid = new UniqueId(id.Uid);
        _ = await FlagsOfAsync(folder, uid, cancellationToken).ConfigureAwait(false) ?? throw new MailMessageNotFoundException("The message is not in the folder.");
        await folder.AddFlagsAsync(uid, MessageFlags.Deleted, silent: true, cancellationToken).ConfigureAwait(false);
        await folder.ExpungeAsync([uid], cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> DeleteMessagesPermanentlyAsync(IReadOnlyList<MessageId> ids, CancellationToken cancellationToken)
    {
        var (folder, present) = await OpenBatchAsync(ids, cancellationToken).ConfigureAwait(false);
        if (present.Count == 0)
        {
            return 0;
        }

        await folder.AddFlagsAsync(present, MessageFlags.Deleted, silent: true, cancellationToken).ConfigureAwait(false);
        await folder.ExpungeAsync(present, cancellationToken).ConfigureAwait(false);
        return present.Count;
    }

    public async Task<int> EmptyFolderAsync(string folderPath, CancellationToken cancellationToken)
    {
        var (folder, _) = await OpenAsync(folderPath, cancellationToken, FolderAccess.ReadWrite).ConfigureAwait(false);
        var all = await folder.SearchAsync(SearchQuery.All, cancellationToken).ConfigureAwait(false);
        if (all.Count == 0)
        {
            return 0;
        }

        await folder.AddFlagsAsync(all, MessageFlags.Deleted, silent: true, cancellationToken).ConfigureAwait(false);
        await folder.ExpungeAsync(all, cancellationToken).ConfigureAwait(false);
        return all.Count;
    }

    public async Task<IReadOnlyList<string>> GetLabelsAsync(CancellationToken cancellationToken)
    {
        var (inbox, _) = await OpenAsync("INBOX", cancellationToken, FolderAccess.ReadWrite).ConfigureAwait(false); // read-only (EXAMINE) lists no permanent flags

        // The server lists every keyword it has ever seen for the folder among its permanent flags, and never forgets one:
        // a label that was renamed or deleted would come back. Only the keywords some message of the inbox carries now count.
        var inUse = new List<string>();
        foreach (var keyword in inbox.PermanentKeywords.Where(k => k.Length > 0 && k[0] is not '$' and not '\\'))
        {
            if ((await inbox.SearchAsync(SearchQuery.HasKeyword(keyword), cancellationToken).ConfigureAwait(false)).Count > 0)
            {
                inUse.Add(keyword);
            }
        }

        return LabelsOf(inUse);
    }

    public async Task<IReadOnlyList<string>> UpdateMessageLabelsAsync(MessageId id, IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(add);
        ArgumentNullException.ThrowIfNull(remove);
        var (folder, uidValidity) = await OpenAsync(id.Folder, cancellationToken, FolderAccess.ReadWrite).ConfigureAwait(false);
        if (id.UidValidity != uidValidity)
        {
            throw new UidValidityChangedException(id.Folder, id.UidValidity, uidValidity);
        }

        var uid = new UniqueId(id.Uid);
        _ = await FlagsOfAsync(folder, uid, cancellationToken).ConfigureAwait(false) ?? throw new MailMessageNotFoundException("The message is not in the folder.");
        if (add.Count > 0)
        {
            await folder.AddFlagsAsync(uid, MessageFlags.None, add.Select(LabelName.ToKeyword).ToHashSet(StringComparer.OrdinalIgnoreCase), silent: true, cancellationToken).ConfigureAwait(false);
        }

        if (remove.Count > 0)
        {
            await folder.RemoveFlagsAsync(uid, MessageFlags.None, remove.Select(LabelName.ToKeyword).ToHashSet(StringComparer.OrdinalIgnoreCase), silent: true, cancellationToken).ConfigureAwait(false);
        }

        var found = await folder.FetchAsync([uid], MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, cancellationToken).ConfigureAwait(false);
        var summary = found.FirstOrDefault(m => m.UniqueId == uid) ?? throw new MailMessageNotFoundException("The message is not in the folder.");
        return LabelsOf(summary.Keywords);
    }

    public async Task WatchInboxAsync(Func<MailboxEvent, ValueTask> onEvent, TimeSpan restartAfter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onEvent);
        var (inbox, uidValidity) = await OpenAsync("INBOX", cancellationToken).ConfigureAwait(false);

        // The notifications come on the thread that waits for the server (nothing else may be sent meanwhile): they are only
        // collected there. The first one ends the wait shortly (so that a burst is answered once), and the answer is made
        // between two waits, when the connection can be asked questions.
        var notices = new MailboxNotices();
        CancellationTokenSource? waiting = null;
        void Wake()
        {
            try
            {
                waiting?.CancelAfter(NoticeDelay);
            }
            catch (ObjectDisposedException)
            {
                // the wait has just ended
            }
        }

        void Count(object? sender, EventArgs e)
        {
            notices.CountChanged();
            Wake();
        }

        void Expunged(object? sender, MessageEventArgs e)
        {
            notices.Expunged();
            Wake();
        }

        void Vanished(object? sender, MessagesVanishedEventArgs e)
        {
            notices.Expunged(e.UniqueIds.Count);
            Wake();
        }

        void Flags(object? sender, MessageFlagsChangedEventArgs e)
        {
            notices.Flags(e.UniqueId?.Id, e.Index, e.Flags, e.Keywords);
            Wake();
        }

        inbox.CountChanged += Count;
        inbox.MessageExpunged += Expunged;
        inbox.MessagesVanished += Vanished;
        inbox.MessageFlagsChanged += Flags;
        try
        {
            var lastUid = (await inbox.SearchAsync(SearchQuery.All, cancellationToken).ConfigureAwait(false)).Select(u => u.Id).DefaultIfEmpty(0u).Max();
            var counters = (Total: inbox.Count, Unread: await UnreadAsync(inbox, cancellationToken).ConfigureAwait(false));
            var idle = client.Capabilities.Contains(ImapCapability.Idle);
            _ = notices.Take(); // what was reported while the folder was being looked at is in the state just read

            while (!cancellationToken.IsCancellationRequested)
            {
                using var done = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                done.CancelAfter(restartAfter);
                waiting = done;
                try
                {
                    if (idle)
                    {
                        await client.IdleAsync(done.Token, CancellationToken.None).ConfigureAwait(false);
                    }
                    else
                    {
                        // No IDLE: a NOOP lets the server report what happened since the last one.
                        try
                        {
                            await Task.Delay(PollInterval, done.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            // the restart time, a notice or the end of the watch
                        }

                        await client.NoOpAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                }
                finally
                {
                    waiting = null;
                }

                var batch = notices.Take();
                if (cancellationToken.IsCancellationRequested || batch.IsEmpty)
                {
                    continue;
                }

                (lastUid, counters) = await AnswerAsync(inbox, uidValidity, batch, lastUid, counters, onEvent, cancellationToken).ConfigureAwait(false);
                _ = notices.Take(); // what our own questions made the server say is already in the answer
            }
        }
        finally
        {
            inbox.CountChanged -= Count;
            inbox.MessageExpunged -= Expunged;
            inbox.MessagesVanished -= Vanished;
            inbox.MessageFlagsChanged -= Flags;
        }
    }

    /// <summary>The most arrivals of one burst that are reported one by one; more than that make the folder "stale".</summary>
    internal const int MaxArrivalsReported = 20;

    /// <summary>After the first notice the wait goes on this long, so that a burst of notices is answered together.</summary>
    internal TimeSpan NoticeDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    private static async Task<(uint LastUid, (int Total, int Unread) Counters)> AnswerAsync(
        IMailFolder inbox,
        uint uidValidity,
        NoticeBatch batch,
        uint lastUid,
        (int Total, int Unread) counters,
        Func<MailboxEvent, ValueTask> onEvent,
        CancellationToken cancellationToken)
    {
        const string Path = "INBOX";
        var events = new List<MailboxEvent>();
        if (batch.Expunged > 0)
        {
            events.Add(new MessagesRemoved(Path, batch.Expunged));
        }

        if (batch.CountChanged)
        {
            var range = new UniqueIdRange(new UniqueId(lastUid + 1), UniqueId.MaxValue);
            // "4:*" also matches the last message when it is below 4 (an IMAP rule): only the really new ones count.
            var arrived = (await inbox.SearchAsync(SearchQuery.Uids(range), cancellationToken).ConfigureAwait(false)).Select(u => u.Id).Where(u => u > lastUid).Order().ToList();
            if (arrived.Count > 0)
            {
                var reported = arrived.TakeLast(MaxArrivalsReported).Select(u => new UniqueId(u)).ToList();
                var fetched = await inbox.FetchAsync(reported, SummaryItems, cancellationToken).ConfigureAwait(false);
                events.AddRange(fetched.OrderBy(m => m.UniqueId.Id).Select(m => new MessageArrived(Path, ToSummary(Path, uidValidity, m))));
                if (arrived.Count > reported.Count)
                {
                    events.Add(new FolderStale(Path));
                }

                lastUid = arrived[^1];
            }
        }

        // Marks the server named by position only: the UID is looked up, which holds only while nothing was removed (positions shift).
        var marked = new Dictionary<uint, MessageMarks>(batch.Marks);
        var unnamed = false;
        if (batch.MarksByPosition.Count > 0)
        {
            if (batch.Expunged > 0 || batch.MarksByPosition.Keys.Any(i => i < 0 || i >= inbox.Count))
            {
                unnamed = true;
            }
            else
            {
                var found = await inbox.FetchAsync([.. batch.MarksByPosition.Keys], MessageSummaryItems.UniqueId, cancellationToken).ConfigureAwait(false);
                foreach (var summary in found)
                {
                    if (batch.MarksByPosition.TryGetValue(summary.Index, out var byPosition))
                    {
                        marked.TryAdd(summary.UniqueId.Id, byPosition);
                    }
                }
            }
        }

        foreach (var (uid, marks) in marked.OrderBy(m => m.Key))
        {
            events.Add(new MessageMarksChanged(
                Path,
                new MessageId(Path, uidValidity, uid),
                marks.Flags.HasFlag(MessageFlags.Seen),
                marks.Flags.HasFlag(MessageFlags.Flagged),
                marks.Flags.HasFlag(MessageFlags.Answered),
                LabelsOf(marks.Keywords)));
        }

        if (unnamed)
        {
            events.Add(new FolderStale(Path));
        }

        var now = (Total: inbox.Count, Unread: await UnreadAsync(inbox, cancellationToken).ConfigureAwait(false));
        if (now != counters)
        {
            events.Add(new FolderCountersChanged(Path, now.Total, now.Unread));
        }

        foreach (var change in events)
        {
            await onEvent(change).ConfigureAwait(false);
        }

        return (lastUid, now);
    }

    private static async Task<int> UnreadAsync(IMailFolder folder, CancellationToken cancellationToken) =>
        (await folder.SearchAsync(SearchQuery.NotSeen, cancellationToken).ConfigureAwait(false)).Count;

    /// <summary>How often a server without IDLE is asked what changed.</summary>
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);

    public async Task<int> RenameLabelAsync(string oldName, string? newName, CancellationToken cancellationToken)
    {
        var oldKeyword = LabelName.ToKeyword(oldName);
        var newKeyword = newName is null ? null : LabelName.ToKeyword(newName);
        if (newKeyword is not null && string.Equals(oldKeyword, newKeyword, StringComparison.OrdinalIgnoreCase))
        {
            return 0; // keywords do not tell capitals apart: there is nothing to change on the server
        }

        var changed = 0;
        foreach (var candidate in await SelectableFoldersAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                _ = await candidate.OpenAsync(FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);
                var found = await candidate.SearchAsync(SearchQuery.HasKeyword(oldKeyword), cancellationToken).ConfigureAwait(false);
                if (found.Count == 0)
                {
                    continue;
                }

                if (newKeyword is not null)
                {
                    await candidate.AddFlagsAsync(found, MessageFlags.None, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { newKeyword }, silent: true, cancellationToken).ConfigureAwait(false);
                }

                await candidate.RemoveFlagsAsync(found, MessageFlags.None, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { oldKeyword }, silent: true, cancellationToken).ConfigureAwait(false);
                changed += found.Count;
            }
            catch (Exception exception) when (exception is ImapCommandException or FolderNotFoundException or InvalidOperationException)
            {
                // A folder that cannot be opened or searched has nothing of ours to rename.
            }
        }

        return changed;
    }

    /// <summary>Every folder of the mailbox that can hold messages, the inbox included, each once.</summary>
    private async Task<List<IMailFolder>> SelectableFoldersAsync(CancellationToken cancellationToken)
    {
        var root = client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : new FolderNamespace('/', string.Empty);
        var listed = await client.GetFoldersAsync(root, StatusItems.None, subscribedOnly: false, cancellationToken).ConfigureAwait(false);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return [.. new List<IMailFolder>(listed) { client.Inbox }
            .Where(f => !f.Attributes.HasFlag(FolderAttributes.NoSelect) && !f.Attributes.HasFlag(FolderAttributes.NonExistent) && seen.Add(f.FullName))];
    }

    public async Task<LabeledMessages> ListLabeledMessagesAsync(string label, int limit, bool includePreview, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var keyword = LabelName.ToKeyword(label);
        var wanted = includePreview ? SummaryItems | MessageSummaryItems.PreviewText : SummaryItems;
        var items = new List<MessageListItem>();
        var total = 0;
        foreach (var candidate in await SelectableFoldersAsync(cancellationToken).ConfigureAwait(false))
        {
            var inbox = IsInbox(candidate);
            if (!inbox && RoleOf(candidate, inbox: false) is FolderRole.Trash or FolderRole.Junk)
            {
                continue; // what was thrown away is not part of "all my mail with this label"
            }

            try
            {
                var path = inbox ? "INBOX" : candidate.FullName;
                var (folder, uidValidity) = await OpenAsync(path, cancellationToken).ConfigureAwait(false);
                var found = (await folder.SearchAsync(SearchQuery.HasKeyword(keyword), cancellationToken).ConfigureAwait(false)).OrderByDescending(u => u.Id).ToList();
                total += found.Count;
                if (found.Count == 0)
                {
                    continue;
                }

                if (!PreviewExtension)
                {
                    client.Capabilities.Disable(ImapCapability.Preview);
                }

                // Only the newest `limit` of a folder can be among the newest `limit` of all of them.
                var fetched = await folder.FetchAsync([.. found.Take(limit)], wanted, cancellationToken).ConfigureAwait(false);
                items.AddRange(fetched.Select(m => ToSummary(path, uidValidity, m)));
            }
            catch (Exception exception) when (exception is MailFolderNotFoundException or ImapCommandException or FolderNotFoundException or InvalidOperationException)
            {
                // A folder that cannot be opened or searched has nothing to show.
            }
        }

        return new LabeledMessages([.. items.OrderByDescending(i => i.Date).ThenByDescending(i => i.Id.Uid).Take(limit)], total);
    }

    private static async Task<MessageFlags?> FlagsOfAsync(IMailFolder folder, UniqueId uid, CancellationToken cancellationToken)
    {
        var found = await folder.FetchAsync([uid], MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, cancellationToken).ConfigureAwait(false);
        return found.FirstOrDefault(m => m.UniqueId == uid) is { } summary ? summary.Flags ?? MessageFlags.None : null;
    }

    private static async Task StoreAsync(IMailFolder folder, UniqueId uid, MessageFlags flag, bool? wanted, CancellationToken cancellationToken)
    {
        if (wanted is true)
        {
            await folder.AddFlagsAsync(uid, flag, silent: true, cancellationToken).ConfigureAwait(false);
        }
        else if (wanted is false)
        {
            await folder.RemoveFlagsAsync(uid, flag, silent: true, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<MessageSource> GetMessageAsync(MessageId id, CancellationToken cancellationToken)
    {
        var (folder, uidValidity) = await OpenAsync(id.Folder, cancellationToken).ConfigureAwait(false);
        if (id.UidValidity != uidValidity)
        {
            throw new UidValidityChangedException(id.Folder, id.UidValidity, uidValidity);
        }

        var uid = new UniqueId(id.Uid);
        var summaries = await folder.FetchAsync([uid], MessageSummaryItems.UniqueId | MessageSummaryItems.Size | MessageSummaryItems.Flags, cancellationToken).ConfigureAwait(false);
        var summary = summaries.FirstOrDefault(m => m.UniqueId.Id == id.Uid) ?? throw new MailMessageNotFoundException("The message is not in the folder.");
        if (summary.Size is { } size && size > maxViewableBytes)
        {
            throw new MailMessageTooLargeException(size);
        }

        byte[] content;
        try
        {
            // BODY.PEEK[]: the mail without the side effect of marking it as read.
            using var stream = await folder.GetStreamAsync(uid, cancellationToken: cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream(checked((int)Math.Min(summary.Size ?? 64 * 1024, maxViewableBytes)));
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxViewableBytes)
                {
                    throw new MailMessageTooLargeException(summary.Size ?? 0);
                }

                buffer.Write(chunk, 0, read);
            }

            content = buffer.ToArray();
        }
        catch (MessageNotFoundException exception)
        {
            throw new MailMessageNotFoundException("The message is not in the folder.", exception);
        }

        var flags = summary.Flags ?? MessageFlags.None;
        return new MessageSource(
            id,
            content,
            flags.HasFlag(MessageFlags.Seen),
            flags.HasFlag(MessageFlags.Flagged),
            flags.HasFlag(MessageFlags.Answered),
            flags.HasFlag(MessageFlags.Draft),
            Labels(summary));
    }

    private static string[] Labels(IMessageSummary message) => LabelsOf(message.Keywords);

    /// <summary>The labels among keywords: the system ones ($..., \\...) are not labels; a keyword of ours is shown by the name it stands for.</summary>
    private static string[] LabelsOf(IEnumerable<string>? keywords) =>
        [.. (keywords ?? []).Where(k => k.Length > 0 && k[0] is not '$' and not '\\').Select(LabelName.FromKeyword).Order(StringComparer.Ordinal)];

    private const MessageSummaryItems SummaryItems =
        MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags | MessageSummaryItems.InternalDate
        | MessageSummaryItems.BodyStructure | MessageSummaryItems.Size;

    private async Task<int> CountUpToAsync(IMailFolder folder, uint uid, bool newestFirst, CancellationToken cancellationToken)
    {
        // Newest first: the cursor message and everything older are left of the page, so count the older ones; the cursor
        // message itself may be gone, which is why it is excluded by UID and not by position.
        if (newestFirst && uid == 1)
        {
            return 0;
        }

        var range = new UniqueIdRange(new UniqueId(1), new UniqueId(newestFirst ? uid - 1 : uid));
        var query = SearchQuery.Uids(range);
        if (client.Capabilities.Contains(ImapCapability.ESearch))
        {
            var result = await folder.SearchAsync(SearchOptions.Count, query, cancellationToken).ConfigureAwait(false);
            return result.Count;
        }

        return (await folder.SearchAsync(query, cancellationToken).ConfigureAwait(false)).Count;
    }

    private static bool IsInboxPath(string path) => string.Equals(path, "INBOX", StringComparison.OrdinalIgnoreCase);

    private static MessageListItem ToSummary(string folder, uint uidValidity, IMessageSummary message)
    {
        var flags = message.Flags ?? MessageFlags.None;
        var envelope = message.Envelope;
        return new MessageListItem(
            new MessageId(folder, uidValidity, message.UniqueId.Id),
            envelope?.From.Mailboxes.Select(ToAddress).FirstOrDefault(),
            [.. (envelope?.To.Mailboxes ?? []).Take(5).Select(ToAddress)],
            envelope?.Subject ?? string.Empty,
            envelope?.Date ?? message.InternalDate ?? DateTimeOffset.UnixEpoch,
            flags.HasFlag(MessageFlags.Seen),
            flags.HasFlag(MessageFlags.Flagged),
            flags.HasFlag(MessageFlags.Answered),
            flags.HasFlag(MessageFlags.Draft),
            message.Attachments.Any(),
            Labels(message),
            message.Size ?? 0,
            PreviewText.Normalize(message.PreviewText));
    }

    private static MailAddressInfo ToAddress(MimeKit.MailboxAddress mailbox) => new(mailbox.Name ?? string.Empty, mailbox.Address);

    public async Task<MailQuota?> GetQuotaAsync(CancellationToken cancellationToken)
    {
        if (!client.Capabilities.Contains(ImapCapability.Quota))
        {
            return null;
        }

        try
        {
            var quota = await client.Inbox.GetQuotaAsync(cancellationToken).ConfigureAwait(false);

            // IMAP reports storage in units of 1024 bytes.
            return quota.StorageLimit is { } limit && quota.CurrentStorageSize is { } used
                ? new MailQuota(used * 1024L, limit * 1024L)
                : null;
        }
        catch (ImapCommandException)
        {
            return null; // the server refused: no quota root for this mailbox
        }
    }

    private static bool IsInbox(IMailFolder folder) =>
        folder.Attributes.HasFlag(FolderAttributes.Inbox) || string.Equals(folder.FullName, "INBOX", StringComparison.OrdinalIgnoreCase);

    private static MailFolderInfo Map(IMailFolder folder, HashSet<string> subscribed)
    {
        var inbox = IsInbox(folder);
        var canContain = !folder.Attributes.HasFlag(FolderAttributes.NoSelect);
        var path = inbox ? "INBOX" : folder.FullName;
        var separator = folder.DirectorySeparator;
        var cut = separator == ' ' ? -1 : path.LastIndexOf(separator);
        return new MailFolderInfo(
            path,
            inbox ? "INBOX" : cut < 0 ? path : path[(cut + 1)..],
            cut < 0 ? null : path[..cut],
            RoleOf(folder, inbox),
            canContain,
            canContain ? folder.Count : 0,
            canContain ? folder.Unread : 0,
            inbox || subscribed.Contains(folder.FullName) || folder.Attributes.HasFlag(FolderAttributes.Subscribed));
    }

    private static FolderRole RoleOf(IMailFolder folder, bool inbox)
    {
        if (inbox)
        {
            return FolderRole.Inbox;
        }

        var attributes = folder.Attributes;
        return attributes.HasFlag(FolderAttributes.Drafts) ? FolderRole.Drafts
            : attributes.HasFlag(FolderAttributes.Sent) ? FolderRole.Sent
            : attributes.HasFlag(FolderAttributes.Archive) ? FolderRole.Archive
            : attributes.HasFlag(FolderAttributes.Junk) ? FolderRole.Junk
            : attributes.HasFlag(FolderAttributes.Trash) ? FolderRole.Trash
            : FolderRole.Other;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (client.IsConnected)
            {
                await client.DisconnectAsync(quit: true, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or MailKit.ProtocolException or MailKit.ServiceNotConnectedException)
        {
            // The connection is being thrown away anyway.
        }
        finally
        {
            client.Dispose();
        }
    }
}
