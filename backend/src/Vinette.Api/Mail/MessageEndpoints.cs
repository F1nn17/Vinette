using Vinette.Api.Errors;
using Vinette.Api.Sessions;
using Vinette.Core.Mail;

namespace Vinette.Api.Mail;

/// <summary>An attached file of a message.</summary>
/// <param name="PartId">The part's address in the message (<c>1.2.1</c>); used to download it.</param>
/// <param name="FileName">A safe plain file name.</param>
/// <param name="ContentType">The type the sender declared; untrusted.</param>
/// <param name="Size">The size in bytes after decoding.</param>
/// <param name="IsMessage">An attached mail.</param>
public sealed record AttachmentDto(string PartId, string FileName, string ContentType, long Size, bool IsMessage);

/// <summary>A message opened for reading.</summary>
/// <param name="Id">The message's identifier.</param>
/// <param name="Subject">The subject; empty if none.</param>
/// <param name="From">The sender; absent if none.</param>
/// <param name="To">All recipients.</param>
/// <param name="Cc">The copy recipients.</param>
/// <param name="Date">When the message was written; absent if the mail has no readable date.</param>
/// <param name="Seen">Read.</param>
/// <param name="Flagged">Marked with a flag.</param>
/// <param name="Answered">Replied to.</param>
/// <param name="Draft">A draft.</param>
/// <param name="Labels">The message's labels.</param>
/// <param name="BodyFormat"><c>html</c>: the sender's HTML, cleaned; <c>text</c>: the plain text made into HTML with links; <c>empty</c>: the mail has no body.</param>
/// <param name="Body">The body as HTML that is safe for a sandboxed frame; empty for <c>empty</c>. External pictures are already taken out.</param>
/// <param name="BlockedExternalResources">How many external loads (pictures, backgrounds) were taken out of the body; more than zero means "show images" has something to show.</param>
/// <param name="Truncated">The body was cut because the mail is very long.</param>
/// <param name="Attachments">The attached files.</param>
public sealed record MessageDetailDto(
    string Id,
    string Subject,
    AddressDto? From,
    IReadOnlyList<AddressDto> To,
    IReadOnlyList<AddressDto> Cc,
    DateTimeOffset? Date,
    bool Seen,
    bool Flagged,
    bool Answered,
    bool Draft,
    IReadOnlyList<string> Labels,
    string BodyFormat,
    string Body,
    int BlockedExternalResources,
    bool Truncated,
    IReadOnlyList<AttachmentDto> Attachments);

public static class MessageEndpoints
{
    public static IEndpointRouteBuilder MapMessages(this IEndpointRouteBuilder api)
    {
        api.MapGet("/messages/{id}", GetAsync)
            .WithName("GetMessage")
            .WithTags("Mail")
            .Produces<MessageDetailDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return api;
    }

    /// <param name="id">The message's identifier from the list (opaque text, use it as it is).</param>
    private static async Task<IResult> GetAsync(
        MessageId id,
        HttpContext http,
        IMailGateway gateway,
        SessionService sessions,
        IMessageParser parser,
        IMessageHtmlSanitizer sanitizer,
        CancellationToken cancellationToken)
    {
        var source = await gateway.ExecuteAsync(
            http.GetMailCredentials(sessions),
            (session, token) => session.GetMessageAsync(id, token),
            cancellationToken);

        using var stream = new MemoryStream(source.Content, writable: false);
        var parsed = await parser.ParseAsync(stream, cancellationToken);
        var (format, body, blocked, truncated) = BodyOf(parsed, sanitizer);

        // What is in a mail is private: nothing in between may keep a copy.
        http.Response.Headers.CacheControl = "private, no-store";
        return Results.Ok(new MessageDetailDto(
            id.ToString(),
            parsed.Subject,
            parsed.From is null ? null : new AddressDto(parsed.From.Name, parsed.From.Address),
            [.. parsed.To.Select(a => new AddressDto(a.Name, a.Address))],
            [.. parsed.Cc.Select(a => new AddressDto(a.Name, a.Address))],
            parsed.Date,
            source.Seen,
            source.Flagged,
            source.Answered,
            source.Draft,
            source.Labels,
            format,
            body,
            blocked,
            truncated,
            [.. parsed.Attachments.Select(a => new AttachmentDto(a.PartId, a.FileName, a.ContentType, a.Size, a.IsMessage))]));
    }

    /// <summary>
    /// The HTML of the mail, cleaned; if it has none, is only blank after cleaning or is refused as hostile, the plain
    /// text made into HTML (and cleaned the same way, so that one rule covers everything that leaves the server).
    /// </summary>
    private static (string Format, string Body, int Blocked, bool Truncated) BodyOf(ParsedMessage parsed, IMessageHtmlSanitizer sanitizer)
    {
        if (parsed.Html is not null)
        {
            var clean = sanitizer.Sanitize(parsed.Html);
            if (!clean.Rejected && (!string.IsNullOrWhiteSpace(clean.Html) || parsed.Text is null))
            {
                return string.IsNullOrWhiteSpace(clean.Html)
                    ? ("empty", string.Empty, clean.BlockedExternalResources, clean.Truncated)
                    : ("html", clean.Html, clean.BlockedExternalResources, clean.Truncated);
            }
        }

        if (!string.IsNullOrWhiteSpace(parsed.Text))
        {
            var linked = PlainTextHtml.ToHtml(parsed.Text, out var cut);
            var clean = sanitizer.Sanitize(linked);
            return ("text", clean.Html, clean.BlockedExternalResources, cut || clean.Truncated);
        }

        return ("empty", string.Empty, 0, false);
    }
}
