using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Vinette.Api.Configuration;
using Vinette.Core.Mail;
using Vinette.Core.Scheduling;

namespace Vinette.Api.Scheduling;

/// <summary>Why a message could not be scheduled.</summary>
public enum ScheduleRefusal
{
    /// <summary>The time is sooner than <c>Scheduling:MinDelay</c> from now (or past).</summary>
    TooSoon,

    /// <summary>The time is later than <c>Scheduling:MaxDelay</c> from now.</summary>
    TooLate,

    /// <summary>The mailbox already has <c>Scheduling:MaxPerMailbox</c> scheduled messages.</summary>
    TooMany,
}

/// <summary>A message was not put in the queue (see <see cref="Reason"/>).</summary>
public sealed class ScheduleRefusedException(ScheduleRefusal reason, string message) : Exception(message)
{
    public ScheduleRefusal Reason { get; } = reason;
}

/// <summary>What became of cancelling a scheduled message.</summary>
public enum CancelOutcome
{
    /// <summary>The message is back among the drafts (or, for a note of a failure, the note is gone).</summary>
    Cancelled,

    /// <summary>There is no such scheduled message.</summary>
    NotFound,

    /// <summary>The mailbox has no drafts folder to put the message in, so it stays scheduled.</summary>
    NoDraftsFolder,
}

/// <summary>
/// The messages that are sent later (VIN-107, ADR-0008): putting one in the queue, listing, cancelling (the message goes back among the drafts), and
/// sending those whose time has come. The mailbox password is kept, encrypted with a limited life, only for a message that waits, and is gone with it;
/// a message is never lost: what cannot be sent goes to the drafts folder.
/// </summary>
public sealed partial class ScheduledMailService(
    IScheduledMailStore store,
    IMessageComposer composer,
    IOutgoingMail outgoing,
    IMailGateway gateway,
    IRecipientSuggestions suggestions,
    IDataProtectionProvider protection,
    IOptions<SchedulingOptions> options,
    ILogger<ScheduledMailService> logger,
    TimeProvider? timeProvider = null)
{
    /// <summary>The purpose isolates these passwords from every other use of the key ring (and from the session's).</summary>
    internal const string Purpose = "Vinette.ScheduledMail.Password.v1";

    private const int MaxSubjectLength = 200;

    private readonly SchedulingOptions settings = options.Value;
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private readonly ITimeLimitedDataProtector protector = protection.CreateProtector(Purpose).ToTimeLimitedDataProtector();

    /// <summary>
    /// Builds the mail now (what cannot be built is refused at once) and puts it in the queue with the password of the session, to go at <paramref name="sendAt"/>.
    /// </summary>
    /// <exception cref="ScheduleRefusedException">The time is too soon or too late, or the mailbox has too many.</exception>
    /// <exception cref="ArgumentException">The message cannot be built (no recipient, an address that is not one, ...).</exception>
    public async Task<ScheduledMail> ScheduleAsync(MailCredentials credentials, OutgoingMessage message, DateTimeOffset sendAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(message);
        var now = time.GetUtcNow();
        if (sendAt < now + settings.MinDelay)
        {
            throw new ScheduleRefusedException(ScheduleRefusal.TooSoon, "The time is too soon.");
        }

        if (sendAt > now + settings.MaxDelay)
        {
            throw new ScheduleRefusedException(ScheduleRefusal.TooLate, "The time is too far ahead.");
        }

        var waiting = (await store.ListAsync(credentials.Address, cancellationToken).ConfigureAwait(false)).Count(m => m.State == ScheduledState.Pending);
        if (waiting >= settings.MaxPerMailbox)
        {
            throw new ScheduleRefusedException(ScheduleRefusal.TooMany, "There are too many scheduled messages.");
        }

        var composed = composer.Compose(message with { Date = sendAt });
        // The password lives until the message is due, the time to try again and a day over; after that nobody can read it, file or no file.
        var lifetime = sendAt - now + settings.RetryFor + TimeSpan.FromDays(1);
        var item = new ScheduledMail(
            Guid.NewGuid().ToString("N"),
            credentials.Address,
            sendAt,
            now,
            Shorten(message.Subject),
            message.To,
            message.Cc,
            message.Bcc,
            composed.MessageId,
            ProtectedPassword: protector.Protect(credentials.Password, lifetime));
        await store.AddAsync(item, composed.Raw, cancellationToken).ConfigureAwait(false);
        return item;
    }

    /// <summary>
    /// The scheduled messages of the mailbox, soonest first. A message that could not be sent and still waits (the mail would not take the password,
    /// so nothing could be done then) is put in the drafts folder now, by this session.
    /// </summary>
    public async Task<IReadOnlyList<ScheduledMail>> ListAsync(MailCredentials credentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var items = await store.ListAsync(credentials.Address, cancellationToken).ConfigureAwait(false);
        var rescued = false;
        foreach (var item in items.Where(i => i.State == ScheduledState.Failed && !i.InDrafts))
        {
            rescued |= await RescueAsync(credentials, item, cancellationToken).ConfigureAwait(false);
        }

        return rescued ? await store.ListAsync(credentials.Address, cancellationToken).ConfigureAwait(false) : items;
    }

    /// <summary>
    /// Cancels a scheduled message: it goes back among the drafts (the text is not lost) and leaves the queue. A note of a failure is simply removed.
    /// </summary>
    public async Task<CancelOutcome> CancelAsync(MailCredentials credentials, string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var item = await store.GetAsync(credentials.Address, id, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return CancelOutcome.NotFound;
        }

        if (!item.InDrafts && await store.ReadRawAsync(item.Id, cancellationToken).ConfigureAwait(false) is { } raw)
        {
            var draft = composer.AddHiddenRecipients(raw, item.Bcc);
            if (!await AppendDraftAsync(credentials, draft, cancellationToken).ConfigureAwait(false))
            {
                return CancelOutcome.NoDraftsFolder;
            }
        }

        _ = await store.RemoveAsync(credentials.Address, item.Id, cancellationToken).ConfigureAwait(false);
        return CancelOutcome.Cancelled;
    }

    /// <summary>Sends the messages whose time has come (and the ones whose next try has). Returns how many it dealt with.</summary>
    public async Task<int> SendDueAsync(CancellationToken cancellationToken)
    {
        var due = await store.DueAsync(time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        foreach (var item in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SendOneAsync(item, cancellationToken).ConfigureAwait(false);
        }

        return due.Count;
    }

    private async Task SendOneAsync(ScheduledMail item, CancellationToken cancellationToken)
    {
        var raw = await store.ReadRawAsync(item.Id, cancellationToken).ConfigureAwait(false);
        if (raw is null)
        {
            _ = await store.RemoveAsync(item.Mailbox, item.Id, cancellationToken).ConfigureAwait(false); // cancelled meanwhile, or damaged: nothing to send
            return;
        }

        string password;
        try
        {
            password = protector.Unprotect(item.ProtectedPassword ?? string.Empty);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            // The keys are gone or the password outlived its time: nobody can log in for this message now.
            await FailAsync(item, "session_revoked", credentials: null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var credentials = new MailCredentials("scheduled-" + item.Id, item.Mailbox, password);
        var everyone = item.To.Concat(item.Cc).Concat(item.Bcc).Select(a => a.Address).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        try
        {
            _ = await outgoing.SendComposedAsync(credentials, new ComposedMessage(raw, new OutgoingEnvelope(item.Mailbox, everyone), item.MessageId), cancellationToken).ConfigureAwait(false);
            _ = await store.RemoveAsync(item.Mailbox, item.Id, CancellationToken.None).ConfigureAwait(false);
            suggestions.Remember(item.Mailbox, item.To.Concat(item.Cc).Concat(item.Bcc));
            Sent(logger);
        }
        catch (MailUnavailableException unavailable) when (unavailable.MessageMayHaveBeenSent)
        {
            await FailAsync(item, "send_uncertain", credentials, cancellationToken).ConfigureAwait(false); // it may have gone: never sent again by itself
        }
        catch (Exception exception) when (exception is MailUnavailableException or MailBusyException)
        {
            await RetryAsync(item, credentials, exception, cancellationToken).ConfigureAwait(false);
        }
        catch (MailAccessRevokedException)
        {
            await FailAsync(item, "session_revoked", credentials: null, cancellationToken).ConfigureAwait(false); // the mail takes the password no more: nothing can be done with it
        }
        catch (MailRejectedException rejected)
        {
            var code = rejected.Reason switch
            {
                SendRejection.Recipient => "recipient_rejected",
                SendRejection.TooLarge => "send_too_large",
                _ => "send_refused",
            };
            await FailAsync(item, code, credentials, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await gateway.ReleaseAsync(credentials.SessionId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task RetryAsync(ScheduledMail item, MailCredentials credentials, Exception cause, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (now > item.SendAt + settings.RetryFor)
        {
            await FailAsync(item, "mail_unavailable", credentials, cancellationToken).ConfigureAwait(false);
            return;
        }

        _ = await store.UpdateAsync(item with { Attempts = item.Attempts + 1, NextAttempt = now + settings.RetryEvery }, CancellationToken.None).ConfigureAwait(false);
        WillRetry(logger, cause.GetType().Name, item.Attempts + 1);
    }

    /// <summary>
    /// The message could not be sent: it is put in the drafts folder if the session can reach it, and what remains is a note without the password
    /// (and, when the drafts folder could not be reached, with the mail, to be put there at the next sign-in).
    /// </summary>
    private async Task FailAsync(ScheduledMail item, string problem, MailCredentials? credentials, CancellationToken cancellationToken)
    {
        var inDrafts = false;
        if (credentials is not null && await store.ReadRawAsync(item.Id, cancellationToken).ConfigureAwait(false) is { } raw)
        {
            try
            {
                inDrafts = await AppendDraftAsync(credentials, composer.AddHiddenRecipients(raw, item.Bcc), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is MailUnavailableException or MailBusyException or MailAccessRevokedException)
            {
                // it stays here, to be put there at the next sign-in
            }
        }

        var failed = item with { State = ScheduledState.Failed, Problem = problem, InDrafts = inDrafts, ProtectedPassword = null, NextAttempt = null };
        if (await store.UpdateAsync(failed, CancellationToken.None).ConfigureAwait(false) && inDrafts)
        {
            await store.DropRawAsync(item.Id, CancellationToken.None).ConfigureAwait(false);
        }

        Failed(logger, problem, inDrafts);
    }

    /// <summary>Puts a failed message that still waits in the drafts folder; true if it did.</summary>
    private async Task<bool> RescueAsync(MailCredentials credentials, ScheduledMail item, CancellationToken cancellationToken)
    {
        if (await store.ReadRawAsync(item.Id, cancellationToken).ConfigureAwait(false) is not { } raw)
        {
            return false;
        }

        try
        {
            if (!await AppendDraftAsync(credentials, composer.AddHiddenRecipients(raw, item.Bcc), cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }
        catch (Exception exception) when (exception is MailUnavailableException or MailBusyException)
        {
            return false; // the next time
        }

        _ = await store.UpdateAsync(item with { InDrafts = true }, cancellationToken).ConfigureAwait(false);
        await store.DropRawAsync(item.Id, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Keeps the mail in the drafts folder as a draft, once (never repeated by the gateway); false if the mailbox has no drafts folder.</summary>
    private async Task<bool> AppendDraftAsync(MailCredentials credentials, ReadOnlyMemory<byte> raw, CancellationToken cancellationToken) =>
        await gateway.ExecuteAsync(
            credentials,
            async (session, token) =>
            {
                var folders = FolderRoleGuesser.Complete(await session.GetFoldersAsync(token).ConfigureAwait(false)); // the same roles as the sidebar shows
                var drafts = folders.FirstOrDefault(f => f.Role == FolderRole.Drafts && f.CanContainMessages);
                if (drafts is null)
                {
                    return false;
                }

                _ = await session.AppendMessageAsync(drafts.Path, raw, seen: true, draft: true, time.GetUtcNow(), token).ConfigureAwait(false);
                return true;
            },
            cancellationToken,
            safeToRepeat: false).ConfigureAwait(false);

    private static string Shorten(string? subject)
    {
        var text = new string([.. (subject ?? string.Empty).Select(c => char.IsControl(c) ? ' ' : c)]);
        return text.Length > MaxSubjectLength ? text[..MaxSubjectLength] : text;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "A scheduled message was sent")]
    private static partial void Sent(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A scheduled message could not be sent ({Problem}); put in the drafts folder: {InDrafts}")]
    private static partial void Failed(ILogger logger, string problem, bool inDrafts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A scheduled message could not be sent now ({Reason}); try number {Attempt} is due later")]
    private static partial void WillRetry(ILogger logger, string reason, int attempt);
}
