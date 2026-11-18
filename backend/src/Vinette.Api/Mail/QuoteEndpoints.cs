using Vinette.Api.Sessions;
using Vinette.Core.Attachments;
using Vinette.Core.Mail;

namespace Vinette.Api.Mail;

/// <summary>A message as the editor needs it to answer or forward it. The words around it ("wrote:", "Re:") are the screen's to choose, in the user's language.</summary>
/// <param name="MessageId">The message's own Message-ID (without angle brackets); absent if it has none. A reply refers to it.</param>
/// <param name="References">The Message-IDs of the conversation up to this message, oldest first.</param>
/// <param name="From">The sender.</param>
/// <param name="ReplyTo">Where the sender asks answers to go; empty if not asked.</param>
/// <param name="To">The recipients.</param>
/// <param name="Cc">The copy recipients.</param>
/// <param name="Date">When it was written.</param>
/// <param name="Subject">The subject, as written.</param>
/// <param name="Text">The plain text; absent if there is none.</param>
/// <param name="Html">The HTML body, cleaned like any mail's; absent if there is none. Pictures from the web are not in it.</param>
/// <param name="Attachments">With <c>attachments=true</c>: the files of the message, now among the mailbox's uploaded files (their ids go into the message being written). Otherwise empty.</param>
public sealed record QuoteDto(
    string? MessageId,
    IReadOnlyList<string> References,
    AddressDto? From,
    IReadOnlyList<AddressDto> ReplyTo,
    IReadOnlyList<AddressDto> To,
    IReadOnlyList<AddressDto> Cc,
    DateTimeOffset? Date,
    string Subject,
    string? Text,
    string? Html,
    IReadOnlyList<Attachments.UploadedAttachmentDto> Attachments);

/// <summary>
/// Answering and forwarding (VIN-102): <c>GET /messages/{id}/quote</c> gives what the editor needs to start a reply, a reply to all or a
/// forward: the headers that make the new message part of the conversation, the recipients to start from, and the body to quote. With
/// <c>attachments=true</c> (a forward) the files of the message are put among the uploaded files.
/// </summary>
public static class QuoteEndpoints
{
    public static IEndpointRouteBuilder MapQuote(this IEndpointRouteBuilder api)
    {
        api.MapGet("/messages/{id}/quote", QuoteAsync)
            .WithName("GetMessageQuote")
            .WithTags("Send")
            .Produces<QuoteDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return api;
    }

    private static AddressDto ToDto(MailAddressInfo a) => new(a.Name, a.Address);

    /// <param name="id">The message's identifier from the list.</param>
    /// <param name="attachments">Put the message's files among the uploaded files and return them (for a forward).</param>
    private static async Task<IResult> QuoteAsync(
        MessageId id,
        HttpContext http,
        SessionService sessions,
        IMailGateway gateway,
        IMessageParser parser,
        IMessageHtmlSanitizer sanitizer,
        IAttachmentStore store,
        CancellationToken cancellationToken,
        bool attachments = false)
    {
        var credentials = http.GetMailCredentials(sessions);
        var source = await gateway.ExecuteAsync(credentials, (session, token) => session.GetMessageAsync(id, token), cancellationToken);

        MemoryStream Open() => new(source.Content, writable: false);
        ParsedMessage parsed;
        await using (var stream = Open())
        {
            parsed = await parser.ParseAsync(stream, cancellationToken);
        }

        var stored = new List<StoredAttachment>();
        if (attachments)
        {
            var files = new List<OutgoingAttachment>();
            foreach (var attachment in parsed.Attachments)
            {
                await using var stream = Open();
                if (await parser.ExtractAttachmentAsync(stream, attachment.PartId, cancellationToken) is { } content)
                {
                    files.Add(new OutgoingAttachment(attachment.FileName, attachment.ContentType, content.Content));
                }
            }

            var (saved, problem) = await StoredFiles.SaveAllAsync(store, credentials.Address, files, cancellationToken);
            if (problem is not null)
            {
                return problem;
            }

            stored = saved;
        }

        var html = string.IsNullOrWhiteSpace(parsed.Html) ? null : sanitizer.Sanitize(parsed.Html);
        return Results.Ok(new QuoteDto(
            parsed.MessageId,
            parsed.References ?? [],
            parsed.From is { } from ? ToDto(from) : null,
            [.. (parsed.ReplyTo ?? []).Select(ToDto)],
            [.. parsed.To.Select(ToDto)],
            [.. parsed.Cc.Select(ToDto)],
            parsed.Date,
            parsed.Subject,
            parsed.Text,
            html is { Rejected: false, Html.Length: > 0 } ? html.Html : null,
            [.. stored.Select(a => new Attachments.UploadedAttachmentDto(a.Id, a.FileName, a.ContentType, a.Size))]));
    }
}
