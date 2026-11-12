using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
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
/// <param name="CanShowFull">The body was cut and asking again with <c>full=true</c> shows more of it.</param>
/// <param name="Sender">What our mail server's checks say about the sender.</param>
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
    bool CanShowFull,
    SenderDto Sender,
    IReadOnlyList<AttachmentDto> Attachments);

/// <summary>The outcome of the sender check.</summary>
/// <param name="Verdict"><c>verified</c>: the domain in the From line passed our server's checks; <c>suspicious</c>: the checks failed (show a warning); <c>unknown</c>: nothing reliable to say (show nothing).</param>
/// <param name="Domain">The domain of the From address the verdict is about; absent if the mail has none.</param>
public sealed record SenderDto(string Verdict, string? Domain);

/// <summary>A change of the marks of a message; only the members that are present change.</summary>
/// <param name="Seen">Read (true) or unread (false).</param>
/// <param name="Flagged">Marked with a flag (true) or not (false).</param>
public sealed record MessageFlagsPatch(bool? Seen, bool? Flagged);

/// <summary>The marks a message has.</summary>
/// <param name="Seen">Read.</param>
/// <param name="Flagged">Marked with a flag.</param>
/// <param name="Answered">Replied to.</param>
/// <param name="Draft">A draft.</param>
public sealed record MessageFlagsDto(bool Seen, bool Flagged, bool Answered, bool Draft);

public static partial class MessageEndpoints
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
        api.MapPatch("/messages/{id}/flags", UpdateFlagsAsync)
            .WithName("UpdateMessageFlags")
            .WithTags("Mail")
            .Produces<MessageFlagsDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        api.MapGet("/messages/{id}/attachments/{partId}", DownloadAsync)
            .WithName("DownloadAttachment")
            .WithTags("Mail")
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return api;
    }

    /// <summary>Marks a message as read or unread and sets or removes its flag. Repeating a change is harmless.</summary>
    /// <param name="id">The message's identifier from the list.</param>
    private static async Task<IResult> UpdateFlagsAsync(
        MessageId id,
        MessageFlagsPatch? patch,
        HttpContext http,
        IMailGateway gateway,
        SessionService sessions,
        CancellationToken cancellationToken)
    {
        var change = new MessageFlagChange(patch?.Seen, patch?.Flagged);
        if (change.IsEmpty)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The body must say what to change: seen, flagged or both.",
                extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.BadRequest });
        }

        var state = await gateway.ExecuteAsync(
            http.GetMailCredentials(sessions),
            (session, token) => session.UpdateFlagsAsync(id, change, token),
            cancellationToken);
        return Results.Ok(new MessageFlagsDto(state.Seen, state.Flagged, state.Answered, state.Draft));
    }

    /// <summary>
    /// An attached file, always as a download: <c>Content-Disposition: attachment</c> with the safe name, a type from a short
    /// list of harmless ones (anything else is plain bytes), <c>nosniff</c> so that the browser does not guess a more
    /// dangerous type, and a CSP that forbids everything in case the file is ever opened as a page. HTML and SVG are never
    /// shown from here.
    /// </summary>
    /// <param name="id">The message's identifier from the list.</param>
    /// <param name="partId">The attachment's <c>partId</c> from the message (<c>1.2.1</c>).</param>
    private static async Task<IResult> DownloadAsync(
        MessageId id,
        string partId,
        HttpContext http,
        IMailGateway gateway,
        SessionService sessions,
        IMessageParser parser,
        CancellationToken cancellationToken)
    {
        if (!PartIdPattern().IsMatch(partId))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "partId is not a part address.", extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.BadRequest });
        }

        var source = await gateway.ExecuteAsync(
            http.GetMailCredentials(sessions),
            (session, token) => session.GetMessageAsync(id, token),
            cancellationToken);

        using var stream = new MemoryStream(source.Content, writable: false);
        var attachment = await parser.ExtractAttachmentAsync(stream, partId, cancellationToken);
        if (attachment is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "There is no such attachment.", extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.NotFound });
        }

        var headers = http.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.CacheControl = "private, no-store";
        headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        headers["Referrer-Policy"] = "no-referrer";
        return Results.File(
            attachment.Content,
            AttachmentContentType.ForDownload(attachment.Attachment.ContentType),
            fileDownloadName: attachment.Attachment.FileName);
    }

    [GeneratedRegex(@"^[0-9]{1,4}(\.[0-9]{1,4}){0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex PartIdPattern();

    /// <param name="id">The message's identifier from the list (opaque text, use it as it is).</param>
    /// <param name="full">Show a very long body in full (up to a limit) instead of its beginning.</param>
    private static async Task<IResult> GetAsync(
        MessageId id,
        HttpContext http,
        IMailGateway gateway,
        SessionService sessions,
        IMessageParser parser,
        IMessageHtmlSanitizer sanitizer,
        SenderVerifier senderVerifier,
        CancellationToken cancellationToken,
        [FromQuery] bool full = false)
    {
        var source = await gateway.ExecuteAsync(
            http.GetMailCredentials(sessions),
            (session, token) => session.GetMessageAsync(id, token),
            cancellationToken);

        using var stream = new MemoryStream(source.Content, writable: false);
        var parsed = await parser.ParseAsync(stream, cancellationToken);
        var (format, body, blocked, truncated) = await BodyOfAsync(parsed, sanitizer, full ? MessageLimits.FullBodyChars : MessageLimits.DefaultBodyChars, cancellationToken);

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
            truncated && !full,
            ToDto(senderVerifier.Check(parsed.AuthenticationResults, parsed.From?.Address)),
            [.. parsed.Attachments.Select(a => new AttachmentDto(a.PartId, a.FileName, a.ContentType, a.Size, a.IsMessage))]));
    }

    private static SenderDto ToDto(SenderCheck check) => new(
        check.Verdict switch
        {
            SenderVerdict.Verified => "verified",
            SenderVerdict.Suspicious => "suspicious",
            _ => "unknown",
        },
        check.Domain);

    /// <summary>
    /// The HTML of the mail, cleaned; if it has none, is only blank after cleaning or is refused as hostile, the plain
    /// text made into HTML (and cleaned the same way, so that one rule covers everything that leaves the server).
    /// </summary>
    /// <summary>
    /// Cleaning is bounded in length (the caller's limit) and in time: markup that keeps the cleaner busy for longer than
    /// <see cref="MessageLimits.SanitizeTimeout"/> is given up on and treated as refused, so a hostile mail cannot tie up the
    /// request (its worker ends by itself, the input being of bounded length).
    /// </summary>
    private static async Task<SanitizedHtml> SanitizeWithinTimeAsync(IMessageHtmlSanitizer sanitizer, string html, Func<string, string?>? resolver, int limit, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() => sanitizer.Sanitize(html, resolver, limit), CancellationToken.None).WaitAsync(MessageLimits.SanitizeTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return new SanitizedHtml(string.Empty, 0, false, true);
        }
    }

    private static async Task<(string Format, string Body, int Blocked, bool Truncated)> BodyOfAsync(ParsedMessage parsed, IMessageHtmlSanitizer sanitizer, int limit, CancellationToken cancellationToken)
    {
        if (parsed.Html is not null)
        {
            var clean = await SanitizeWithinTimeAsync(sanitizer, parsed.Html, new InlineImageResolver(parsed.InlineParts).Resolve, limit, cancellationToken);
            if (!clean.Rejected && (!string.IsNullOrWhiteSpace(clean.Html) || parsed.Text is null))
            {
                return string.IsNullOrWhiteSpace(clean.Html)
                    ? ("empty", string.Empty, clean.BlockedExternalResources, clean.Truncated)
                    : ("html", clean.Html, clean.BlockedExternalResources, clean.Truncated);
            }
        }

        if (!string.IsNullOrWhiteSpace(parsed.Text))
        {
            var linked = PlainTextHtml.ToHtml(parsed.Text, out var cut, limit);
            var clean = await SanitizeWithinTimeAsync(sanitizer, linked, null, IMessageHtmlSanitizer.MaxInputLength, cancellationToken);
            return ("text", clean.Html, clean.BlockedExternalResources, cut || clean.Truncated);
        }

        return ("empty", string.Empty, 0, false);
    }
}
