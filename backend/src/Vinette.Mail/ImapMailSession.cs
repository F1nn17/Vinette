using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Vinette.Core.Mail;

namespace Vinette.Mail;

/// <summary>
/// One authenticated IMAP connection. Not thread-safe by design: the connection manager lets only one command run at a time.
/// </summary>
internal sealed class ImapMailSession(ImapClient client, bool previewExtension = true) : IMailSession
{
    static ImapMailSession() => LegacyCharsets.Ensure();

    private bool PreviewExtension => previewExtension;

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
        IMailFolder folder;
        try
        {
            folder = IsInboxPath(query.Folder) ? client.Inbox : await client.GetFolderAsync(query.Folder, cancellationToken).ConfigureAwait(false);
            _ = await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FolderNotFoundException or ImapCommandException or InvalidOperationException)
        {
            // A missing folder and a folder that cannot be opened (a container) look the same to the caller.
            throw new MailFolderNotFoundException("The folder does not exist or cannot hold messages.", exception);
        }

        var uidValidity = (uint)folder.UidValidity;
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
                client.Capabilities &= ~ImapCapabilities.Preview; // test switch: use the fallback that reads the beginning of the body
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
                client.Capabilities &= ~ImapCapabilities.Preview;
            }

            var fetched = await folder.FetchAsync([.. pageUids.Select(u => new UniqueId(u))], wanted, cancellationToken).ConfigureAwait(false);
            IEnumerable<IMessageSummary> inOrder = query.NewestFirst ? fetched.OrderByDescending(m => m.UniqueId.Id) : fetched.OrderBy(m => m.UniqueId.Id);
            items.AddRange(inOrder.Select(m => ToSummary(query.Folder, uidValidity, m)));
        }

        MessageCursor? next = left.Count > pageUids.Count && items.Count > 0 ? new MessageCursor(uidValidity, items[^1].Id.Uid) : null;
        return new MessagePage(uidValidity, items, next, matching.Count);
    }

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
        if (client.Capabilities.HasFlag(ImapCapabilities.ESearch))
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
            [.. (message.Keywords is { } keywords ? keywords : new HashSet<string>()).Where(k => k.Length > 0 && k[0] is not '$' and not '\\').Order(StringComparer.Ordinal)],
            message.Size ?? 0,
            PreviewText.Normalize(message.PreviewText));
    }

    private static MailAddressInfo ToAddress(MimeKit.MailboxAddress mailbox) => new(mailbox.Name ?? string.Empty, mailbox.Address);

    public async Task<MailQuota?> GetQuotaAsync(CancellationToken cancellationToken)
    {
        if (!client.Capabilities.HasFlag(ImapCapabilities.Quota))
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
