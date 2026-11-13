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
    /// Moves several messages of one folder to another with one command (see <see cref="MoveMessageAsync"/> for the two ways).
    /// Returns the messages that were moved; one that was no longer in the folder is simply not among them.
    /// </summary>
    /// <exception cref="ArgumentException">The messages are not one batch (<see cref="MessageBatch.IsValid"/>).</exception>
    /// <exception cref="MailFolderNotFoundException">There is no such folder (the source or the target), or it cannot hold messages.</exception>
    /// <exception cref="UidValidityChangedException">The ids belong to an earlier incarnation of the folder.</exception>
    Task<IReadOnlyList<MovedMessage>> MoveMessagesAsync(IReadOnlyList<MessageId> ids, string targetFolder, CancellationToken cancellationToken);

    /// <summary>
    /// Changes the read and flag marks of several messages of one folder with one command per mark. Returns the messages that
    /// were changed; one that was no longer in the folder is not among them.
    /// </summary>
    /// <exception cref="ArgumentException">The messages are not one batch (<see cref="MessageBatch.IsValid"/>).</exception>
    /// <exception cref="MailFolderNotFoundException">There is no such folder, or it cannot hold messages.</exception>
    /// <exception cref="UidValidityChangedException">The ids belong to an earlier incarnation of the folder.</exception>
    Task<IReadOnlyList<MessageId>> UpdateFlagsOfManyAsync(IReadOnlyList<MessageId> ids, MessageFlagChange change, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes one message for good (marks it deleted and removes it from its folder, that message alone). There is no way back:
    /// callers allow it only for the trash.
    /// </summary>
    /// <exception cref="MailFolderNotFoundException">There is no such folder, or it cannot hold messages.</exception>
    /// <exception cref="UidValidityChangedException">The id belongs to an earlier incarnation of the folder.</exception>
    /// <exception cref="MailMessageNotFoundException">The message is not in the folder (any more).</exception>
    Task DeleteMessagePermanentlyAsync(MessageId id, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes several messages of one folder for good with one command each for marking and removing. There is no way back: callers allow it
    /// only for the trash. Returns how many were deleted; one that was no longer in the folder is not counted.
    /// </summary>
    /// <exception cref="ArgumentException">The messages are not one batch (<see cref="MessageBatch.IsValid"/>).</exception>
    /// <exception cref="MailFolderNotFoundException">There is no such folder, or it cannot hold messages.</exception>
    /// <exception cref="UidValidityChangedException">The ids belong to an earlier incarnation of the folder.</exception>
    Task<int> DeleteMessagesPermanentlyAsync(IReadOnlyList<MessageId> ids, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes every message of a folder for good and returns how many there were. There is no way back: callers allow it only for the trash and
    /// the spam folder.
    /// </summary>
    /// <exception cref="MailFolderNotFoundException">There is no such folder, or it cannot hold messages.</exception>
    Task<int> EmptyFolderAsync(string folder, CancellationToken cancellationToken);

    /// <summary>
    /// The labels that messages of the inbox carry now (by the names the keywords stand for), without the system ones; the labels of messages in
    /// other folders are not found this way (the user's own list, kept in the settings, has those that were put on in Vinette).
    /// </summary>
    Task<IReadOnlyList<string>> GetLabelsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Puts labels on a message and takes labels off it (IMAP STORE of keywords) and returns the labels it has afterwards. Adding what is
    /// there and removing what is not is not an error. The names must be valid (<see cref="LabelName"/>).
    /// </summary>
    /// <exception cref="MailFolderNotFoundException">There is no such folder, or it cannot hold messages.</exception>
    /// <exception cref="UidValidityChangedException">The id belongs to an earlier incarnation of the folder.</exception>
    /// <exception cref="MailMessageNotFoundException">The message is not in the folder (any more).</exception>
    Task<IReadOnlyList<string>> UpdateMessageLabelsAsync(MessageId id, IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove, CancellationToken cancellationToken);

    /// <summary>
    /// The newest messages that carry a label, from every folder except the trash and the spam folder (IMAP SEARCH KEYWORD in each).
    /// Messages are ordered by date, newest first, and there is no further page: <see cref="LabeledMessages.Total"/> says if some are not shown.
    /// A folder that cannot be searched is skipped.
    /// </summary>
    Task<LabeledMessages> ListLabeledMessagesAsync(string label, int limit, bool includePreview, CancellationToken cancellationToken);

    /// <summary>
    /// Renames a label on every message of every folder, or, with <paramref name="newName"/> null, takes it off every message. Returns how many
    /// messages changed. A folder that cannot be searched is skipped.
    /// </summary>
    Task<int> RenameLabelAsync(string oldName, string? newName, CancellationToken cancellationToken);

    /// <summary>
    /// Watches the inbox and reports what the server says about it (IMAP IDLE; on a server without it, a check every few seconds),
    /// until <paramref name="cancellationToken"/> is cancelled or the connection breaks (which throws). The server is asked to
    /// end an IDLE and start a new one every <paramref name="restartAfter"/>, as servers drop an IDLE that lasts too long. While it
    /// runs the connection can do nothing else, so it is a connection of its own. <paramref name="onChange"/> runs for every
    /// notification, one after another. <paramref name="state"/> is what an earlier connection of the same watch knew: if it is
    /// <see cref="MailboxWatchState.Known"/> and the folder is the same, what happened since is found out first and reported (with
    /// CONDSTORE and QRESYNC, exactly; without, as far as counting goes, and "stale" for the rest); the session keeps it up to date.
    /// </summary>
    Task WatchInboxAsync(Func<MailboxEvent, ValueTask> onChange, TimeSpan restartAfter, MailboxWatchState state, CancellationToken cancellationToken);

    /// <summary>
    /// How much of the mailbox's storage is used (IMAP GETQUOTAROOT). Null when the server has no quota extension or
    /// sets no storage limit for this mailbox: then there is nothing to show.
    /// </summary>
    Task<MailQuota?> GetQuotaAsync(CancellationToken cancellationToken);
}
