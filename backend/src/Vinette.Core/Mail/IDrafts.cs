namespace Vinette.Core.Mail;

/// <summary>A draft that was kept in the drafts folder.</summary>
/// <param name="Id">The draft's id in the drafts folder; null if the mail server does not say what it is (look in the folder). A draft saved again has a new id: the old one is gone.</param>
/// <param name="MessageId">The Message-ID of the draft (without angle brackets); the same for every saved version, so that a draft stays one message.</param>
/// <param name="SavedAt">When this version was kept.</param>
/// <param name="PreviousRemoved">False when an earlier version was named but could not be removed (the folder then holds both until the next save).</param>
public sealed record SavedDraft(MessageId? Id, string MessageId, DateTimeOffset SavedAt, bool PreviousRemoved);

/// <summary>A draft taken out of the drafts folder, ready to be continued: what was written and the files attached to it.</summary>
/// <param name="Id">The draft's id.</param>
/// <param name="Message">What the user wrote (the sender is the one the draft was saved with).</param>
public sealed record OpenedDraft(MessageId Id, OutgoingMessage Message);

/// <summary>
/// Drafts live in the mailbox's drafts folder as ordinary mail, so that they are the same in every client: saving builds the mail and puts it
/// there (IMAP APPEND, flagged as a draft), then removes the version it replaces. Only messages of the drafts folder are touched.
/// </summary>
public interface IDrafts
{
    /// <summary>
    /// Keeps <paramref name="message"/> as a draft; nobody has to be named yet. When <paramref name="previous"/> is given, that version is removed
    /// once the new one is safe (a version that is already gone is not an error). Returns null if the mailbox has no drafts folder (none is created).
    /// </summary>
    /// <exception cref="ArgumentException">The message cannot be built (an address that is not an address, too many recipients, a subject that is too long).</exception>
    /// <exception cref="MailMessageNotFoundException"><paramref name="previous"/> is not in the drafts folder: nothing is saved.</exception>
    Task<SavedDraft?> SaveAsync(MailCredentials credentials, OutgoingMessage message, MessageId? previous, CancellationToken cancellationToken);

    /// <summary>Takes a draft apart to continue it, with its attached files. Null if the mailbox has no drafts folder.</summary>
    /// <exception cref="MailMessageNotFoundException">The message is not in the drafts folder (any more).</exception>
    /// <exception cref="UidValidityChangedException">The id belongs to an earlier incarnation of the folder.</exception>
    /// <exception cref="MailMessageTooLargeException">The draft is larger than a mail may be to be opened.</exception>
    Task<OpenedDraft?> OpenAsync(MailCredentials credentials, MessageId id, CancellationToken cancellationToken);

    /// <summary>Deletes a draft for good (it is the user's own unsent text: it does not go to the trash). False if the mailbox has no drafts folder.</summary>
    /// <exception cref="MailMessageNotFoundException">The message is not in the drafts folder (any more).</exception>
    Task<bool> DeleteAsync(MailCredentials credentials, MessageId id, CancellationToken cancellationToken);
}
