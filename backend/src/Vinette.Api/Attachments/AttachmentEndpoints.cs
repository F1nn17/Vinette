using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Vinette.Api.Errors;
using Vinette.Core.Attachments;

namespace Vinette.Api.Attachments;

/// <summary>An uploaded file that waits to be attached to a message.</summary>
/// <param name="Id">Opaque identifier; the message that is sent names its attachments by it.</param>
/// <param name="FileName">The name the recipient will see (cleaned).</param>
/// <param name="ContentType">The type it will be sent with.</param>
/// <param name="Size">Size in bytes.</param>
public sealed record UploadedAttachmentDto(string Id, string FileName, string ContentType, long Size);

/// <summary>What may be uploaded, per mailbox.</summary>
/// <param name="MaxFileBytes">The biggest single file.</param>
/// <param name="MaxTotalBytes">The most all the waiting files may take together.</param>
/// <param name="MaxFiles">The most files that may wait.</param>
public sealed record AttachmentLimitsDto(long MaxFileBytes, long MaxTotalBytes, int MaxFiles);

/// <summary>The files that wait, and the limits.</summary>
public sealed record AttachmentsResponse(IReadOnlyList<UploadedAttachmentDto> Items, AttachmentLimitsDto Limits);

/// <summary>
/// Uploading files to attach to a message (VIN-093): <c>POST /attachments</c> takes one file as <c>multipart/form-data</c>, read as it
/// comes (never held whole in memory), <c>GET /attachments</c> lists what waits and the limits, <c>DELETE /attachments/{id}</c> takes a
/// file off (this is also how an upload is cancelled once it has finished). Every file belongs to the mailbox that uploaded it.
/// </summary>
public static class AttachmentEndpoints
{
    /// <summary>What the request may carry besides the file itself (boundaries, the part's headers).</summary>
    private const long EnvelopeAllowance = 1024 * 1024;

    public static IEndpointRouteBuilder MapAttachments(this IEndpointRouteBuilder api)
    {
        api.MapPost("/attachments", UploadAsync)
            .WithName("UploadAttachment")
            .WithTags("Attachments")
            .Produces<UploadedAttachmentDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);
        api.MapGet("/attachments", ListAsync)
            .WithName("ListAttachments")
            .WithTags("Attachments")
            .Produces<AttachmentsResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);
        api.MapDelete("/attachments/{id}", RemoveAsync)
            .WithName("RemoveAttachment")
            .WithTags("Attachments")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);
        return api;
    }

    private static UploadedAttachmentDto ToDto(StoredAttachment a) => new(a.Id, a.FileName, a.ContentType, a.Size);

    private static AttachmentLimitsDto ToDto(AttachmentLimits l) => new(l.MaxFileBytes, l.MaxTotalBytes, l.MaxFiles);

    /// <summary>
    /// Takes one file (the first part of the form that has a file name). The limits are applied while the file is read, so an
    /// oversized upload is stopped early and leaves nothing: 413 <c>attachment_too_large</c> (one file), 413
    /// <c>attachments_too_large</c> (all together), 409 <c>too_many_attachments</c>.
    /// </summary>
    private static async Task<IResult> UploadAsync(HttpRequest request, ClaimsPrincipal user, IAttachmentStore store, CancellationToken cancellationToken)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var media)
            || !string.Equals(media.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return Problem(StatusCodes.Status400BadRequest, "The file must be sent as multipart/form-data.", ErrorCodes.BadRequest);
        }

        var boundary = HeaderUtilities.RemoveQuotes(media.Boundary).Value;
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 200)
        {
            return Problem(StatusCodes.Status400BadRequest, "The form has no usable boundary.", ErrorCodes.BadRequest);
        }

        // The server's own ceiling on a request is only a backstop; the store counts the bytes of the file as they arrive.
        var ceiling = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (ceiling is { IsReadOnly: false })
        {
            ceiling.MaxRequestBodySize = store.Limits.MaxFileBytes + EnvelopeAllowance;
        }

        try
        {
            var reader = new MultipartReader(boundary, request.Body) { HeadersCountLimit = 16, HeadersLengthLimit = 16 * 1024 };
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(cancellationToken)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) || !disposition.IsFileDisposition())
                {
                    continue; // a field that is not a file
                }

                var name = disposition.FileNameStar.HasValue ? disposition.FileNameStar.Value : HeaderUtilities.RemoveQuotes(disposition.FileName).Value;
                var stored = await store.SaveAsync(user.Identity!.Name!, name ?? string.Empty, section.ContentType, section.Body, cancellationToken);
                return Results.Created($"/api/v1/attachments/{stored.Id}", ToDto(stored));
            }

            return Problem(StatusCodes.Status400BadRequest, "The form has no file.", ErrorCodes.BadRequest);
        }
        catch (AttachmentRefusedException refused)
        {
            return refused.Reason switch
            {
                AttachmentRefusal.FileTooLarge => Problem(StatusCodes.Status413PayloadTooLarge, "The file is too large.", ErrorCodes.AttachmentTooLarge),
                AttachmentRefusal.TotalTooLarge => Problem(StatusCodes.Status413PayloadTooLarge, "The attached files together are too large.", ErrorCodes.AttachmentsTooLarge),
                _ => Problem(StatusCodes.Status409Conflict, "Too many files are attached.", ErrorCodes.TooManyAttachments),
            };
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return Problem(StatusCodes.Status413PayloadTooLarge, "The file is too large.", ErrorCodes.AttachmentTooLarge);
        }
        catch (InvalidDataException)
        {
            return Problem(StatusCodes.Status400BadRequest, "The form is malformed.", ErrorCodes.BadRequest);
        }
    }

    /// <summary>The files that wait for the signed-in mailbox, and the limits (so that the screen can refuse a file before sending it).</summary>
    private static async Task<AttachmentsResponse> ListAsync(ClaimsPrincipal user, IAttachmentStore store, CancellationToken cancellationToken)
    {
        var items = await store.ListAsync(user.Identity!.Name!, cancellationToken);
        return new AttachmentsResponse([.. items.Select(ToDto)], ToDto(store.Limits));
    }

    /// <summary>Takes a file off; 404 if there is no such file among the signed-in mailbox's (somebody else's is no file).</summary>
    /// <param name="id">The file's identifier.</param>
    private static async Task<IResult> RemoveAsync(string id, ClaimsPrincipal user, IAttachmentStore store, CancellationToken cancellationToken) =>
        await store.RemoveAsync(user.Identity!.Name!, id, cancellationToken)
            ? Results.NoContent()
            : Problem(StatusCodes.Status404NotFound, "There is no such attachment.", ErrorCodes.NotFound);

    private static IResult Problem(int status, string title, string code) =>
        Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });
}
