namespace Vinette.Core.Mail;

/// <summary>
/// Finds the special folders of a server that does not say which they are (no SPECIAL-USE): by the names mail programs
/// and providers use, in English and Russian. The server's own attributes always win, and a role is given to one
/// folder only, so a user's own "Archive" subfolder is not taken for the archive.
/// </summary>
public static class FolderRoleGuesser
{
    private static readonly (FolderRole Role, string[] Names)[] KnownNames =
    [
        (FolderRole.Drafts, ["Drafts", "Draft", "Черновики", "Черновик"]),
        (FolderRole.Sent, ["Sent", "Sent Items", "Sent Messages", "Sent Mail", "Отправленные", "Отправленные письма", "Отправленная почта"]),
        (FolderRole.Archive, ["Archive", "Archives", "Архив", "Архивы"]),
        (FolderRole.Junk, ["Junk", "Spam", "Junk E-mail", "Junk Email", "Bulk Mail", "Спам", "Нежелательная почта", "Нежелательные"]),
        (FolderRole.Trash, ["Trash", "Deleted Items", "Deleted Messages", "Deleted", "Bin", "Корзина", "Удалённые", "Удаленные", "Удалённые письма", "Удаленные письма"]),
    ];

    // Containers some servers put the special folders into: "INBOX.Sent" (Courier, Cyrus), "[Gmail]/Spam", "[Google Mail]/Trash".
    private static readonly string[] Containers = ["INBOX", "[Gmail]", "[Google Mail]"];

    /// <summary>Returns the folders with the roles the server did not give filled in by name.</summary>
    public static IReadOnlyList<MailFolderInfo> Complete(IReadOnlyList<MailFolderInfo> folders)
    {
        var taken = folders.Where(f => f.Role != FolderRole.Other).Select(f => f.Role).ToHashSet();
        var guessed = new Dictionary<string, FolderRole>(StringComparer.Ordinal);

        foreach (var (role, names) in KnownNames)
        {
            if (taken.Contains(role))
            {
                continue;
            }

            // The first name of the list wins when several folders match ("Spam" before "Junk E-mail" is not a priority, "Drafts" before "Draft" is).
            var found = names
                .Select(name => folders.FirstOrDefault(f => f.Role == FolderRole.Other && f.CanContainMessages && !guessed.ContainsKey(f.Path) && IsCandidate(f, name)))
                .FirstOrDefault(f => f is not null);
            if (found is not null)
            {
                guessed[found.Path] = role;
            }
        }

        return guessed.Count == 0
            ? folders
            : [.. folders.Select(f => guessed.TryGetValue(f.Path, out var role) ? f with { Role = role } : f)];
    }

    private static bool IsCandidate(MailFolderInfo folder, string name) =>
        string.Equals(folder.Name, name, StringComparison.OrdinalIgnoreCase)
        && (folder.ParentPath is null || Containers.Contains(folder.ParentPath, StringComparer.OrdinalIgnoreCase));
}
