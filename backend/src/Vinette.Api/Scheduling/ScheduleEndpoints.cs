using Vinette.Api.Errors;
using Vinette.Api.Mail;
using Vinette.Api.Sessions;
using Vinette.Core.Attachments;
using Vinette.Core.Mail;
using Vinette.Core.Scheduling;

namespace Vinette.Api.Scheduling;

/// <summary>A message to send later: the same members as a message to send now, and the time.</summary>
/// <param name="SendAt">When it goes (an instant, with its offset). At least <c>Scheduling:MinDelay</c> from now and at most <c>Scheduling:MaxDelay</c>.</param>
/// <param name="FromName">The name to show with the sender's address (the sender itself is always the signed-in mailbox).</param>
/// <param name="To">The main recipients.</param>
/// <param name="Cc">The copy recipients.</param>
/// <param name="Bcc">The hidden recipients.</param>
/// <param name="Subject">The subject.</param>
/// <param name="Text">The plain text.</param>
/// <param name="Html">The HTML body, if the message is formatted.</param>
/// <param name="AttachmentIds">The ids of uploaded files to attach.</param>
/// <param name="InReplyTo">The Message-ID of the message this is a reply to.</param>
/// <param name="References">The Message-IDs of the conversation so far.</param>
/// <param name="DraftId">The draft this message was written from: deleted once the message is in the queue.</param>
/// <param name="RequestReceipt">Ask the recipients for a read receipt.</param>
public sealed record ScheduleRequest(
    DateTimeOffset? SendAt,
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

/// <summary>A message that is sent later.</summary>
/// <param name="Id">The id, for cancelling it.</param>
/// <param name="SendAt">When it goes.</param>
/// <param name="Subject">The subject.</param>
/// <param name="To">The main recipients.</param>
/// <param name="Cc">The copy recipients (the hidden ones are not shown).</param>
/// <param name="State"><c>pending</c> (waits for its time) or <c>failed</c> (could not be sent).</param>
/// <param name="Problem">For a failed one: the code of what went wrong (a code of the API, such as <c>recipient_rejected</c> or <c>mail_unavailable</c>).</param>
/// <param name="InDrafts">For a failed one: it is among the drafts (otherwise it will be put there at the next sign-in).</param>
public sealed record ScheduledDto(string Id, DateTimeOffset SendAt, string Subject, IReadOnlyList<AddressDto> To, IReadOnlyList<AddressDto> Cc, string State, string? Problem, bool InDrafts);

/// <summary>A message was put in the queue.</summary>
/// <param name="Item">The scheduled message.</param>
/// <param name="DraftRemoved">False if the draft it was written from could not be deleted (it stays among the drafts).</param>
public sealed record ScheduleResponse(ScheduledDto Item, bool DraftRemoved);

/// <summary>The scheduled messages of the mailbox, soonest first.</summary>
/// <param name="Items">The messages.</param>
public sealed record ScheduledListResponse(IReadOnlyList<ScheduledDto> Items);

/// <summary>
/// Sending later (VIN-107, ADR-0008): <c>POST /scheduled</c> puts a message in the queue, <c>GET /scheduled</c> lists the queue,
/// <c>DELETE /scheduled/{id}</c> cancels a message (it goes back among the drafts).
/// </summary>
public static class ScheduleEndpoints
{
    public static IEndpointRouteBuilder MapScheduled(this IEndpointRouteBuilder api)
    {
        api.MapPost("/scheduled", ScheduleAsync)
            .WithName("ScheduleMessage")
            .WithTags("Scheduled")
            .Produces<ScheduleResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict);
        api.MapGet("/scheduled", ListAsync)
            .WithName("ListScheduled")
            .WithTags("Scheduled")
            .Produces<ScheduledListResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        api.MapDelete("/scheduled/{id}", CancelAsync)
            .WithName("CancelScheduled")
            .WithTags("Scheduled")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return api;
    }

    private static ScheduledDto ToDto(ScheduledMail m) => new(
        m.Id,
        m.SendAt,
        m.Subject,
        [.. m.To.Select(a => new AddressDto(a.Name, a.Address))],
        [.. m.Cc.Select(a => new AddressDto(a.Name, a.Address))],
        m.State == ScheduledState.Failed ? "failed" : "pending",
        m.Problem,
        m.InDrafts);

    /// <summary>
    /// Puts a message in the queue. 400 for a time that is missing, too soon (<c>schedule_too_soon</c>) or too far ahead (<c>schedule_too_late</c>), and for what
    /// cannot be built (no recipient, an address that is not one, an unknown file or draft); 409 <c>too_many_scheduled</c> when the mailbox has too many.
    /// </summary>
    private static async Task<IResult> ScheduleAsync(
        ScheduleRequest request,
        HttpContext http,
        SessionService sessions,
        ScheduledMailService scheduled,
        IDrafts drafts,
        IAttachmentStore store,
        IMessageHtmlSanitizer sanitizer,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        if (request.SendAt is not { } sendAt)
        {
            return Problem(StatusCodes.Status400BadRequest, "When to send the message is missing.", ErrorCodes.BadRequest);
        }

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

        if (OutgoingHtml.Clean(request.Html, sanitizer, out var html) is { } badHtml)
        {
            return badHtml;
        }

        var message = SendEndpoints.ToMessage(
            new SendRequest(request.FromName, request.To, request.Cc, request.Bcc, request.Subject, request.Text, request.Html, request.AttachmentIds, request.InReplyTo, request.References, request.DraftId, request.RequestReceipt),
            credentials,
            files,
            html);
        ScheduledMail item;
        try
        {
            item = await scheduled.ScheduleAsync(credentials, message, sendAt, cancellationToken);
        }
        catch (ScheduleRefusedException refused)
        {
            return refused.Reason switch
            {
                ScheduleRefusal.TooSoon => Problem(StatusCodes.Status400BadRequest, "The time is too soon: pick a later one.", ErrorCodes.ScheduleTooSoon),
                ScheduleRefusal.TooLate => Problem(StatusCodes.Status400BadRequest, "The time is too far ahead.", ErrorCodes.ScheduleTooLate),
                _ => Problem(StatusCodes.Status409Conflict, "There are too many scheduled messages.", ErrorCodes.TooManyScheduled),
            };
        }
        catch (ArgumentException)
        {
            return Problem(StatusCodes.Status400BadRequest, "The message needs a recipient, and an address that is not an address, too many recipients or too long a subject cannot be sent.", ErrorCodes.BadRequest);
        }

        // The message is safe in the queue: what follows must not fail the answer.
        var draftRemoved = await SendEndpoints.TidyAsync(request.AttachmentIds, draft, credentials, store, drafts, loggers);
        return Results.Created($"/api/v1/scheduled/{item.Id}", new ScheduleResponse(ToDto(item), draftRemoved));
    }

    /// <summary>The scheduled messages, soonest first. A failed one that still waits is put among the drafts by this call.</summary>
    private static async Task<IResult> ListAsync(HttpContext http, SessionService sessions, ScheduledMailService scheduled, CancellationToken cancellationToken)
    {
        var items = await scheduled.ListAsync(http.GetMailCredentials(sessions), cancellationToken);
        return Results.Ok(new ScheduledListResponse([.. items.Select(ToDto)]));
    }

    /// <summary>Cancels a message: it goes back among the drafts and leaves the queue (a note of a failure is just removed). 404 if there is none; 409 <c>no_target_folder</c> without a drafts folder (it stays queued).</summary>
    private static async Task<IResult> CancelAsync(string id, HttpContext http, SessionService sessions, ScheduledMailService scheduled, CancellationToken cancellationToken) =>
        await scheduled.CancelAsync(http.GetMailCredentials(sessions), id, cancellationToken) switch
        {
            CancelOutcome.Cancelled => Results.NoContent(),
            CancelOutcome.NoDraftsFolder => Problem(StatusCodes.Status409Conflict, "The mailbox has no folder for drafts, so the message stays scheduled.", ErrorCodes.NoTargetFolder),
            _ => Problem(StatusCodes.Status404NotFound, "There is no such scheduled message.", ErrorCodes.NotFound),
        };

    private static IResult Problem(int status, string title, string code) =>
        Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });
}
