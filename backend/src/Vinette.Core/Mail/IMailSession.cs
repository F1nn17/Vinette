namespace Vinette.Core.Mail;

/// <summary>
/// One authenticated connection to the user's mailbox. A session is never used by two callers at the same time:
/// <see cref="IMailGateway"/> serializes the commands. Mail operations (folders, messages) are added here by the
/// features that need them; callers only ever see this interface, never the protocol library behind it.
/// </summary>
public interface IMailSession : IAsyncDisposable
{
    /// <summary>True while the connection to the mail server is open and authenticated.</summary>
    bool IsConnected { get; }

    /// <summary>Keeps the connection alive and detects a dead one (the IMAP NOOP command).</summary>
    Task PingAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Every folder of the mailbox with its message and unread counters (IMAP LIST and STATUS), unsorted; see <see cref="FolderOrder"/>.
    /// </summary>
    Task<IReadOnlyList<MailFolderInfo>> GetFoldersAsync(CancellationToken cancellationToken);

    /// <summary>
    /// One page of the messages of a folder, newest or oldest first, by arrival. The pages are stable: new mail does not
    /// shift them, because the cursor is a UID and not a position.
    /// </summary>
    /// <exception cref="MailFolderNotFoundException">There is no such folder, or it cannot hold messages.</exception>
    /// <exception cref="UidValidityChangedException">The cursor belongs to an earlier incarnation of the folder.</exception>
    Task<MessagePage> ListMessagesAsync(MessageListQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// How much of the mailbox's storage is used (IMAP GETQUOTAROOT). Null when the server has no quota extension or
    /// sets no storage limit for this mailbox: then there is nothing to show.
    /// </summary>
    Task<MailQuota?> GetQuotaAsync(CancellationToken cancellationToken);
}
