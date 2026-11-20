using Vinette.Core.Mail;

namespace Vinette.Core.Scheduling;

/// <summary>Where a scheduled message is.</summary>
public enum ScheduledState
{
    /// <summary>Waits for its time (or for the next try).</summary>
    Pending,

    /// <summary>It could not be sent. What remains is a note for the user: the message itself was put in the drafts folder, or, if the mail would not take the password any more, still waits here without the password to be put there at the next sign-in.</summary>
    Failed,
}

/// <summary>A message that is sent later (ADR-0008).</summary>
/// <param name="Id">Opaque id.</param>
/// <param name="Mailbox">The mailbox it goes from.</param>
/// <param name="SendAt">When it goes.</param>
/// <param name="CreatedAt">When it was scheduled.</param>
/// <param name="Subject">The subject, for the list.</param>
/// <param name="To">The main recipients, for the list.</param>
/// <param name="Cc">The copy recipients.</param>
/// <param name="Bcc">The hidden recipients: not in the mail, kept so that a message put back among the drafts keeps them.</param>
/// <param name="MessageId">The Message-ID of the mail.</param>
/// <param name="State">Waiting or failed.</param>
/// <param name="Attempts">How many times sending was tried and the mail server could not be reached.</param>
/// <param name="NextAttempt">When the next try is due; absent for the first one (at <paramref name="SendAt"/>).</param>
/// <param name="Problem">For a failed message: a short code of what went wrong (a code of the API, for the screen to explain).</param>
/// <param name="InDrafts">For a failed message: it was put in the drafts folder (otherwise it still waits here, without a password).</param>
/// <param name="ProtectedPassword">The mailbox password, encrypted with a limited life (ADR-0008); absent once it is no longer needed. Never leaves the server.</param>
public sealed record ScheduledMail(
    string Id,
    string Mailbox,
    DateTimeOffset SendAt,
    DateTimeOffset CreatedAt,
    string Subject,
    IReadOnlyList<MailAddressInfo> To,
    IReadOnlyList<MailAddressInfo> Cc,
    IReadOnlyList<MailAddressInfo> Bcc,
    string MessageId,
    ScheduledState State = ScheduledState.Pending,
    int Attempts = 0,
    DateTimeOffset? NextAttempt = null,
    string? Problem = null,
    bool InDrafts = false,
    string? ProtectedPassword = null)
{
    /// <summary>When the message is next due: the next try, or the time it was scheduled for.</summary>
    public DateTimeOffset DueAt => NextAttempt ?? SendAt;
}

/// <summary>Keeps the messages that are sent later: the description of each and the mail itself.</summary>
public interface IScheduledMailStore
{
    /// <summary>Keeps a new message and its mail.</summary>
    Task AddAsync(ScheduledMail item, ReadOnlyMemory<byte> raw, CancellationToken cancellationToken);

    /// <summary>The scheduled messages of the mailbox, soonest first (failed ones too).</summary>
    Task<IReadOnlyList<ScheduledMail>> ListAsync(string mailbox, CancellationToken cancellationToken);

    /// <summary>One of the mailbox's messages; null if there is none (or it belongs to another mailbox).</summary>
    Task<ScheduledMail?> GetAsync(string mailbox, string id, CancellationToken cancellationToken);

    /// <summary>The messages, of every mailbox, that wait and whose time has come.</summary>
    Task<IReadOnlyList<ScheduledMail>> DueAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The mail of a message; null if it has none (any more).</summary>
    Task<byte[]?> ReadRawAsync(string id, CancellationToken cancellationToken);

    /// <summary>Replaces the description of a message that exists (the mail is not touched); false if it is gone.</summary>
    Task<bool> UpdateAsync(ScheduledMail item, CancellationToken cancellationToken);

    /// <summary>Removes the message, its mail and its password; false if there was none.</summary>
    Task<bool> RemoveAsync(string mailbox, string id, CancellationToken cancellationToken);

    /// <summary>Removes the mail of a message and keeps its description (a failed one that was put in the drafts folder).</summary>
    Task DropRawAsync(string id, CancellationToken cancellationToken);
}
