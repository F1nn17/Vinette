using Vinette.Api.Errors;
using Vinette.Api.Sessions;
using Vinette.Core.Attachments;
using Vinette.Core.Mail;

namespace Vinette.Api.Mail;

/// <summary>A message to send.</summary>
/// <param name="FromName">The name to show with the sender's address (the sender itself is always the signed-in mailbox).</param>
/// <param name="To">The main recipients.</param>
/// <param name="Cc">The copy recipients.</param>
/// <param name="Bcc">The hidden recipients.</param>
/// <param name="Subject">The subject.</param>
/// <param name="Text">The plain text.</param>
/// <param name="Html">The HTML body, if the message is formatted.</param>
/// <param name="AttachmentIds">The ids of uploaded files (<c>POST /attachments</c>) to attach.</param>
/// <param name="InReplyTo">The Message-ID of the message this is a reply to.</param>
/// <param name="References">The Message-IDs of the conversation so far.</param>
/// <param name="DraftId">The draft this message was written from (its last <c>id</c> from <c>POST /drafts</c>): deleted once the message is out.</param>
/// <param name="RequestReceipt">Ask the recipients for a read receipt (a Disposition-Notification-To header naming the sender). Off by default.</param>
public sealed record SendRequest(
    string? FromName,
    IReadOnlyList<AddressDto>? To,
    IReadOnlyList<AddressDto>? Cc,
    IReadOnlyList<AddressDto>? Bcc,
    string? Subject,
    string? Text,
    string? Html,
    IReadOnlyList<string>? AttachmentIds,
    string? InReplyTo,
    IReadOnlyList<string>? References,
    string? DraftId,
    bool RequestReceipt = false);

/// <summary>A message that went out.</summary>
/// <param name="MessageId">The Message-ID of the message that was sent.</param>
/// <param name="Copy">What became of the copy in the "Sent" folder: <c>saved</c>, <c>no_sent_folder</c> (the mailbox has none) or <c>failed</c>.</param>
/// <param name="CopyId">The id of the copy in the "Sent" folder, if it is known.</param>
/// <param name="DraftRemoved">False if the draft it was written from could not be deleted (it stays in the drafts folder).</param>
public sealed record SentDto(string MessageId, string Copy, string? CopyId, bool DraftRemoved);

/// <summary>
/// Sending (VIN-138): <c>POST /messages</c> sends a message from the signed-in mailbox, keeps a copy in "Sent", and tidies up: the uploaded
/// files it used and the draft it was written from are removed, and the recipients are remembered for suggestions. Once the message is out
/// nothing that follows can fail the answer, and a failed send is never repeated by the server.
/// </summary>
public static partial class SendEndpoints
{
    /// <summary>How long the tidying after a send may take; it is not tied to the request, which may be gone by then.</summary>
    private static readonly TimeSpan TidyTimeout = TimeSpan.FromSeconds(30);

    public static IEndpointRouteBuilder MapSend(this IEndpointRouteBuilder api)
    {
        api.MapPost("/messages", SendAsync)
            .WithName("SendMessage")
            .WithTags("Send")
            .Produces<SentDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return api;
    }

    internal static MailAddressInfo ToInfo(AddressDto a) => new(a.Name ?? string.Empty, a.Address ?? string.Empty);

    private static string CopyName(SentCopyOutcome outcome) => outcome switch
    {
        SentCopyOutcome.Saved => "saved",
        SentCopyOutcome.NoSentFolder => "no_sent_folder",
        _ => "failed",
    };

    /// <summary>
    /// 400 for what cannot be built or sent as asked (no recipient, a malformed address, too many recipients, a subject that is too long, an
    /// unknown file or draft id); 422 <c>recipient_rejected</c> (with the address) or <c>send_refused</c> when the mail server refuses the
    /// recipient, the sender or the message; 413 <c>send_too_large</c>; 503 <c>send_uncertain</c> when the connection broke after the message
    /// was handed over (it may have gone: look in "Sent" before sending again), and 503 <c>mail_unavailable</c> when it did not.
    /// </summary>
    private static async Task<IResult> SendAsync(
        SendRequest request,
        HttpContext http,
        SessionService sessions,
        IOutgoingMail outgoing,
        IDrafts drafts,
        IAttachmentStore store,
        IRecipientSuggestions suggestions,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        MessageId? draft = null;
        if (!string.IsNullOrEmpty(request.DraftId))
        {
            if (!MessageId.TryParse(request.DraftId, out var parsed))
            {
                return Problem(StatusCodes.Status400BadRequest, "The draft is not a message id.", ErrorCodes.BadRequest);
            }

            draft = parsed;
        }

        var credentials = http.GetMailCredentials(sessions);
        var files = await OutgoingFiles.LoadAsync(store, credentials.Address, request.AttachmentIds, cancellationToken);
        if (files is null)
        {
            return Problem(StatusCodes.Status400BadRequest, "An attached file is not there (any more).", ErrorCodes.BadRequest);
        }

        var message = ToMessage(request, credentials, files);

        SendOutcome outcome;
        try
        {
            outcome = await outgoing.SendAsync(credentials, message, cancellationToken);
        }
        catch (ArgumentException)
        {
            return Problem(StatusCodes.Status400BadRequest, "The message needs a recipient, and an address that is not an address, too many recipients or too long a subject cannot be sent.", ErrorCodes.BadRequest);
        }
        catch (MailRejectedException rejected)
        {
            return Rejected(rejected);
        }
        catch (MailUnavailableException unavailable) when (unavailable.MessageMayHaveBeenSent)
        {
            return Problem(StatusCodes.Status503ServiceUnavailable, "The connection broke while the message was being handed over; it may have been sent. Look in the sent messages before sending it again.", ErrorCodes.SendUncertain);
        }

        // The message is out: what follows must not fail the answer.
        suggestions.Remember(credentials.Address, [.. message.To, .. message.Cc, .. message.Bcc]);
        var draftRemoved = await TidyAsync(request.AttachmentIds, draft, credentials, store, drafts, loggers);

        return Results.Ok(new SentDto(outcome.Message.MessageId, CopyName(outcome.Copy), outcome.CopyId?.ToString(), draftRemoved));
    }

    /// <summary>The message as the user wrote it, from the mailbox that is signed in.</summary>
    internal static OutgoingMessage ToMessage(SendRequest request, MailCredentials credentials, List<OutgoingAttachment> files) =>
        new(
            new MailAddressInfo(request.FromName ?? string.Empty, credentials.Address),
            [.. (request.To ?? []).Select(ToInfo)],
            [.. (request.Cc ?? []).Select(ToInfo)],
            [.. (request.Bcc ?? []).Select(ToInfo)],
            request.Subject ?? string.Empty,
            request.Text,
            request.Html,
            files,
            request.InReplyTo,
            request.References,
            RequestReceipt: request.RequestReceipt);

    /// <summary>
    /// What follows once a message is out or is safe in the queue: the uploaded files it used are removed and the draft it was written from is
    /// deleted. Nothing here may fail the answer, and a client that has gone away must not stop it. False if the draft could not be deleted.
    /// </summary>
    internal static async Task<bool> TidyAsync(
        IReadOnlyList<string>? attachmentIds,
        MessageId? draft,
        MailCredentials credentials,
        IAttachmentStore store,
        IDrafts drafts,
        ILoggerFactory loggers)
    {
        var logger = loggers.CreateLogger("Vinette.Api.Mail.Send");
        using var timeout = new CancellationTokenSource(TidyTimeout);
        foreach (var id in (attachmentIds ?? []).Distinct(StringComparer.Ordinal))
        {
            try
            {
                _ = await store.RemoveAsync(credentials.Address, id, timeout.Token);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                FileNotRemoved(logger, exception.GetType().Name);
            }
        }

        if (draft is null)
        {
            return true;
        }

        try
        {
            _ = await drafts.DeleteAsync(credentials, draft.Value, timeout.Token);
            return true;
        }
        catch (Exception exception) when (exception is MailUnavailableException or MailBusyException or MailAccessRevokedException or OperationCanceledException)
        {
            DraftNotDeleted(logger, exception.GetType().Name);
            return false;
        }
    }

    private static IResult Rejected(MailRejectedException rejected) => rejected.Reason switch
    {
        SendRejection.Recipient => Results.Problem(
            statusCode: StatusCodes.Status422UnprocessableEntity,
            title: "The mail server does not accept a recipient.",
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.RecipientRejected, ["address"] = rejected.Address }),
        SendRejection.TooLarge => Problem(StatusCodes.Status413PayloadTooLarge, "The message is too large for the mail server.", ErrorCodes.SendTooLarge),
        _ => Problem(StatusCodes.Status422UnprocessableEntity, "The mail server refused the message.", ErrorCodes.SendRefused),
    };

    private static IResult Problem(int status, string title, string code) =>
        Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });

    [LoggerMessage(Level = LogLevel.Warning, Message = "A file of a sent message could not be removed ({Reason}); it is removed when it expires")]
    private static partial void FileNotRemoved(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The draft of a sent message could not be deleted ({Reason})")]
    private static partial void DraftNotDeleted(ILogger logger, string reason);
}
