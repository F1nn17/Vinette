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
    /// The raw mail and the flags of one message. Reading does not mark the message as read. A mail larger than
    /// <see cref="MessageLimits.MaxViewableBytes"/> is not downloaded.
    /// </summary>
    /// <exception cref="MailFolderNotFoundException">There is no such folder, or it cannot hold messages.</exception>
    /// <exception cref="UidValidityChangedException">The id belongs to an earlier incarnation of the folder.</exception>
    /// <exception cref="MailMessageNotFoundException">The message is not in the folder (any more).</exception>
    /// <exception cref="MailMessageTooLargeException">The mail is larger than the limit.</exception>
    Task<MessageSource> GetMessageAsync(MessageId id, CancellationToken cancellationToken);

    /// <summary>
    /// Changes the read and flag marks of one message (IMAP STORE) and returns the flags it has afterwards. Setting a mark that
    /// is already set is not an error.
    /// </summary>
    /// <exception cref="MailFolderNotFoundException">There is no such folder, or it cannot hold messages.</exception>
    /// <exception cref="UidValidityChangedException">The id belongs to an earlier incarnation of the folder.</exception>
    /// <exception cref="MailMessageNotFoundException">The message is not in the folder (any more).</exception>
    Task<MessageFlagState> UpdateFlagsAsync(MessageId id, MessageFlagChange change, CancellationToken cancellationToken);

    /// <summary>
    /// Moves one message to another folder (IMAP MOVE; on a server without it COPY, then the original is marked deleted and
    /// removed from the folder, for that message alone) and returns the message's id in the new folder, or null if the
    /// server does not say what it is (no UIDPLUS). The message is never in neither folder: a failed copy leaves it where it was.
    /// </summary>
    /// <exception cref="MailFolderNotFoundException">There is no such folder (the source or the target), or it cannot hold messages.</exception>
    /// <exception cref="UidValidityChangedException">The id belongs to an earlier incarnation of the folder.</exception>
    /// <exception cref="MailMessageNotFoundException">The message is not in the folder (any more).</exception>
    Task<MessageId?> MoveMessageAsync(MessageId id, string targetFolder, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes one message for good (marks it deleted and removes it from its folder, that message alone). There is no way back:
    /// callers allow it only for the trash.
    /// </summary>
    /// <exception cref="MailFolderNotFoundException">There is no such folder, or it cannot hold messages.</exception>
    /// <exception cref="UidValidityChangedException">The id belongs to an earlier incarnation of the folder.</exception>
    /// <exception cref="MailMessageNotFoundException">The message is not in the folder (any more).</exception>
    Task DeleteMessagePermanentlyAsync(MessageId id, CancellationToken cancellationToken);

    /// <summary>
    /// How much of the mailbox's storage is used (IMAP GETQUOTAROOT). Null when the server has no quota extension or
    /// sets no storage limit for this mailbox: then there is nothing to show.
    /// </summary>
    Task<MailQuota?> GetQuotaAsync(CancellationToken cancellationToken);
}
