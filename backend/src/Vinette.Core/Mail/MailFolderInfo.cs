namespace Vinette.Core.Mail;

/// <summary>What a folder is for. Taken from the server's SPECIAL-USE attributes; names are guessed only when it has none.</summary>
public enum FolderRole
{
    /// <summary>An ordinary user folder.</summary>
    Other = 0,
    Inbox,
    Drafts,
    Sent,
    Archive,
    Junk,
    Trash,
}

/// <summary>One folder of the mailbox with the counters the sidebar shows.</summary>
/// <param name="Path">The full folder name as the server has it ("Work/Reports"); the folder's identifier in the API.</param>
/// <param name="Name">The last part of <paramref name="Path"/>, for display.</param>
/// <param name="ParentPath">The path of the folder this one is inside; null at the top level.</param>
/// <param name="Role">What the folder is for.</param>
/// <param name="CanContainMessages">False for folders that only group others (\Noselect).</param>
/// <param name="Total">Messages in the folder; 0 when it cannot hold messages.</param>
/// <param name="Unread">Unread messages in the folder; 0 when it cannot hold messages.</param>
/// <param name="Subscribed">The user (or the server's default setup) subscribed to the folder.</param>
public sealed record MailFolderInfo(
    string Path,
    string Name,
    string? ParentPath,
    FolderRole Role,
    bool CanContainMessages,
    int Total,
    int Unread,
    bool Subscribed);

public static class FolderOrder
{
    private static readonly FolderRole[] RoleOrder =
        [FolderRole.Inbox, FolderRole.Drafts, FolderRole.Sent, FolderRole.Archive, FolderRole.Junk, FolderRole.Trash];

    /// <summary>
    /// The order of the sidebar: the special folders first (inbox, drafts, sent, archive, spam, trash), then the user's
    /// folders by name; inside a parent the same rule, children right after their parent.
    /// </summary>
    public static IReadOnlyList<MailFolderInfo> Sort(IEnumerable<MailFolderInfo> folders)
    {
        var byParent = folders.ToLookup(f => f.ParentPath ?? string.Empty, StringComparer.Ordinal);
        var result = new List<MailFolderInfo>();
        var known = new HashSet<string>(StringComparer.Ordinal);
        var all = byParent.SelectMany(g => g).ToList();
        foreach (var folder in all)
        {
            _ = known.Add(folder.Path);
        }

        void Walk(string parent)
        {
            foreach (var folder in byParent[parent].OrderBy(Rank).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(f => f.Path, StringComparer.Ordinal))
            {
                result.Add(folder);
                Walk(folder.Path);
            }
        }

        Walk(string.Empty);

        // A folder whose parent the server did not list (it is hidden, or not subscribed) still must not vanish.
        foreach (var orphan in all.Where(f => f.ParentPath is not null && !known.Contains(f.ParentPath)).OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            result.Add(orphan);
            Walk(orphan.Path);
        }

        return result;
    }

    private static int Rank(MailFolderInfo folder)
    {
        var index = Array.IndexOf(RoleOrder, folder.Role);
        return index < 0 ? RoleOrder.Length : index;
    }
}
