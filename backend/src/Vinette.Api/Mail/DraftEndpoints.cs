using System.Security.Claims;
using Vinette.Api.Errors;
using Vinette.Api.Sessions;
using Vinette.Core.Attachments;
using Vinette.Core.Mail;

namespace Vinette.Api.Mail;

/// <summary>A draft as the editor keeps it. Nothing is required: a draft may be a single word.</summary>
/// <param name="PreviousId">The id of the version this one replaces (from the last save); that version is removed once this one is kept. Absent for the first save.</param>
/// <param name="MessageId">The <c>messageId</c> the last save returned, so that the draft stays one message; absent for the first save.</param>
/// <param name="FromName">The name to show with the sender's address (the sender itself is always the signed-in mailbox).</param>
/// <param name="To">The main recipients.</param>
/// <param name="Cc">The copy recipients.</param>
/// <param name="Bcc">The hidden recipients.</param>
/// <param name="Subject">The subject.</param>
/// <param name="Text">The plain text.</param>
/// <param name="Html">The HTML body, if the message is formatted.</param>
/// <param name="AttachmentIds">The ids of uploaded files (<c>POST /attachments</c>) that belong to the draft.</param>
/// <param name="InReplyTo">The Message-ID of the message this is a reply to.</param>
/// <param name="References">The Message-IDs of the conversation so far.</param>
/// <param name="RequestReceipt">Ask for a read receipt when the message is sent (kept in the draft).</param>
public sealed record DraftRequest(
    string? PreviousId,
    string? MessageId,
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
    bool RequestReceipt = false);

/// <summary>A draft that was kept.</summary>
/// <param name="Id">The draft's id now (the earlier one is gone); absent if the mail server does not say what it is.</param>
/// <param name="MessageId">The draft's Message-ID: send it back with the next save.</param>
/// <param name="SavedAt">When this version was kept.</param>
/// <param name="PreviousRemoved">False if the earlier version could not be removed (it goes with the next save).</param>
public sealed record DraftSavedDto(string? Id, string MessageId, DateTimeOffset SavedAt, bool PreviousRemoved);

/// <summary>A draft opened to be continued. Its files wait among the mailbox's uploaded files, under the ids listed here.</summary>
/// <param name="Id">The draft's id.</param>
/// <param name="MessageId">The draft's Message-ID: send it back with the next save.</param>
/// <param name="From">The sender.</param>
/// <param name="To">The main recipients.</param>
/// <param name="Cc">The copy recipients.</param>
/// <param name="Bcc">The hidden recipients.</param>
/// <param name="Subject">The subject.</param>
/// <param name="Text">The plain text; absent if there is none.</param>
/// <param name="Html">The HTML body, cleaned; absent if there is none.</param>
/// <param name="Attachments">The attached files, as uploaded files (their ids go back with the next save).</param>
/// <param name="InReplyTo">The Message-ID of the message this is a reply to.</param>
/// <param name="References">The Message-IDs of the conversation so far.</param>
/// <param name="Date">When the draft was last kept; absent if unknown.</param>
/// <param name="RequestReceipt">The draft asks for a read receipt.</param>
public sealed record DraftDto(
    string Id,
    string? MessageId,
    AddressDto From,
    IReadOnlyList<AddressDto> To,
    IReadOnlyList<AddressDto> Cc,
    IReadOnlyList<AddressDto> Bcc,
    string Subject,
    string? Text,
    string? Html,
    IReadOnlyList<Attachments.UploadedAttachmentDto> Attachments,
    string? InReplyTo,
    IReadOnlyList<string> References,
    DateTimeOffset? Date,
    bool RequestReceipt = false);

/// <summary>
/// Drafts (VIN-094): <c>POST /drafts</c> keeps a draft in the mailbox's drafts folder (and removes the version it replaces),
/// <c>GET /drafts/{id}</c> opens one to continue it (its files come back as uploaded files), <c>DELETE /drafts/{id}</c> deletes one for
/// good. Only messages of the drafts folder can be reached this way.
/// </summary>
public static class DraftEndpoints
{
    public static IEndpointRouteBuilder MapDrafts(this IEndpointRouteBuilder api)
    {
        api.MapPost("/drafts", SaveAsync)
            .WithName("SaveDraft")
            .WithTags("Drafts")
            .Produces<DraftSavedDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        api.MapGet("/drafts/{id}", OpenAsync)
            .WithName("OpenDraft")
            .WithTags("Drafts")
            .Produces<DraftDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        api.MapDelete("/drafts/{id}", DeleteAsync)
            .WithName("DeleteDraft")
            .WithTags("Drafts")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return api;
    }

    private static MailAddressInfo ToInfo(AddressDto a) => new(a.Name ?? string.Empty, a.Address ?? string.Empty);

    private static AddressDto ToDto(MailAddressInfo a) => new(a.Name, a.Address);

    /// <summary>
    /// Keeps the draft. 400 for what cannot be built (a malformed address, too many recipients, a subject that is too long, an
    /// unknown or malformed id); 404 if the version to replace is not in the drafts folder; 409 <c>no_target_folder</c> if the
    /// mailbox has no drafts folder.
    /// </summary>
    private static async Task<IResult> SaveAsync(
        DraftRequest request,
        HttpContext http,
        ClaimsPrincipal user,
        SessionService sessions,
        IDrafts drafts,
        IAttachmentStore store,
        IMessageHtmlSanitizer sanitizer,
        CancellationToken cancellationToken)
    {
        MessageId? previous = null;
        if (!string.IsNullOrEmpty(request.PreviousId))
        {
            if (!MessageId.TryParse(request.PreviousId, out var parsed))
            {
                return Problem(StatusCodes.Status400BadRequest, "The previous version is not a message id.", ErrorCodes.BadRequest);
            }

            previous = parsed;
        }

        var credentials = http.GetMailCredentials(sessions);
        var files = await OutgoingFiles.LoadAsync(store, credentials.Address, request.AttachmentIds, cancellationToken);
        if (files is null)
        {
            return Problem(StatusCodes.Status400BadRequest, "An attached file is not there (any more).", ErrorCodes.BadRequest);
        }

        if (OutgoingHtml.Clean(request.Html, sanitizer, out var html) is { } refused)
        {
            return refused;
        }

        var message = new OutgoingMessage(
            new MailAddressInfo(request.FromName ?? string.Empty, credentials.Address),
            [.. (request.To ?? []).Select(ToInfo)],
            [.. (request.Cc ?? []).Select(ToInfo)],
            [.. (request.Bcc ?? []).Select(ToInfo)],
            request.Subject ?? string.Empty,
            request.Text,
            html,
            files,
            request.InReplyTo,
            request.References,
            request.MessageId,
            RequestReceipt: request.RequestReceipt);
        try
        {
            var saved = await drafts.SaveAsync(credentials, message, previous, cancellationToken);
            return saved is null
                ? Problem(StatusCodes.Status409Conflict, "The mailbox has no folder for drafts.", ErrorCodes.NoTargetFolder)
                : Results.Ok(new DraftSavedDto(saved.Id?.ToString(), saved.MessageId, saved.SavedAt, saved.PreviousRemoved));
        }
        catch (ArgumentException)
        {
            // What the composer refuses is data the user typed; its text names no secret but is no use to the client either.
            return Problem(StatusCodes.Status400BadRequest, "The draft has an address that is not an address, too many recipients or too long a subject.", ErrorCodes.BadRequest);
        }
    }

    /// <summary>
    /// Opens the draft: its files are put among the mailbox's uploaded files (new ids), so that the editor can go on as if it had never been
    /// closed. The HTML goes through the same cleaning as the HTML of any mail.
    /// </summary>
    private static async Task<IResult> OpenAsync(
        MessageId id,
        HttpContext http,
        SessionService sessions,
        IDrafts drafts,
        IAttachmentStore store,
        IMessageHtmlSanitizer sanitizer,
        CancellationToken cancellationToken)
    {
        var credentials = http.GetMailCredentials(sessions);
        var opened = await drafts.OpenAsync(credentials, id, cancellationToken);
        if (opened is null)
        {
            return Problem(StatusCodes.Status404NotFound, "There is no such draft.", ErrorCodes.NotFound);
        }

        var message = opened.Message;
        var (stored, problem) = await StoredFiles.SaveAllAsync(store, credentials.Address, message.Attachments, cancellationToken);
        if (problem is not null)
        {
            return problem; // none of the draft's files is left behind (the draft itself stays where it is)
        }

        var html = string.IsNullOrWhiteSpace(message.Html) ? null : sanitizer.Sanitize(message.Html);
        return Results.Ok(new DraftDto(
            opened.Id.ToString(),
            message.MessageId,
            ToDto(message.From),
            [.. message.To.Select(ToDto)],
            [.. message.Cc.Select(ToDto)],
            [.. message.Bcc.Select(ToDto)],
            message.Subject,
            message.Text,
            html is { Rejected: false, Html.Length: > 0 } ? html.Html : null,
            [.. stored.Select(a => new Attachments.UploadedAttachmentDto(a.Id, a.FileName, a.ContentType, a.Size))],
            message.InReplyTo,
            message.References ?? [],
            message.Date,
            message.RequestReceipt));
    }

    /// <summary>Deletes the draft for good; 404 if it is not in the drafts folder (any more).</summary>
    private static async Task<IResult> DeleteAsync(MessageId id, HttpContext http, SessionService sessions, IDrafts drafts, CancellationToken cancellationToken) =>
        await drafts.DeleteAsync(http.GetMailCredentials(sessions), id, cancellationToken)
            ? Results.NoContent()
            : Problem(StatusCodes.Status404NotFound, "There is no such draft.", ErrorCodes.NotFound);

    private static IResult Problem(int status, string title, string code) =>
        Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });
}
