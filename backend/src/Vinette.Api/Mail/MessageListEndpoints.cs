using Microsoft.AspNetCore.Mvc;
using Vinette.Api.Errors;
using Vinette.Api.Sessions;
using Vinette.Core.Mail;

namespace Vinette.Api.Mail;

/// <summary>A person on a message.</summary>
public sealed record AddressDto(string Name, string Address);

/// <summary>One line of the message list.</summary>
/// <param name="Id">The message's identifier (see docs/architecture.md): opaque text, use it as it is.</param>
/// <param name="From">The sender; absent if the message has none.</param>
/// <param name="To">The first recipients.</param>
/// <param name="Subject">The subject; empty if none.</param>
/// <param name="Date">When the message was written.</param>
/// <param name="Seen">Read.</param>
/// <param name="Flagged">Marked with a flag.</param>
/// <param name="Answered">Replied to.</param>
/// <param name="Draft">A draft.</param>
/// <param name="HasAttachments">Has attached files.</param>
/// <param name="Labels">The message's labels (keywords).</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="Preview">The beginning of the text in one line (up to 200 characters); empty if none or <c>preview=false</c> was passed.</param>
public sealed record MessageListItemDto(
    string Id,
    AddressDto? From,
    IReadOnlyList<AddressDto> To,
    string Subject,
    DateTimeOffset Date,
    bool Seen,
    bool Flagged,
    bool Answered,
    bool Draft,
    bool HasAttachments,
    IReadOnlyList<string> Labels,
    long Size,
    string Preview);

/// <summary>A page of messages in arrival order.</summary>
/// <param name="Items">The messages in the requested order.</param>
/// <param name="Next">Pass as <c>after</c> to get the next page; absent on the last page.</param>
/// <param name="Total">Messages in the whole folder.</param>
public sealed record MessageListResponse(IReadOnlyList<MessageListItemDto> Items, string? Next, int Total);

public static class MessageListEndpoints
{
    public static IEndpointRouteBuilder MapMessageList(this IEndpointRouteBuilder api)
    {
        api.MapGet("/folders/{folder}/messages", ListAsync)
            .WithName("ListMessages")
            .WithTags("Mail")
            .Produces<MessageListResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return api;
    }

    /// <param name="folder">The folder's path (URL-encoded, "/" as %2F).</param>
    /// <param name="limit">Messages per page, 1 to 100 (default 50).</param>
    /// <param name="after">The <c>next</c> of the previous page; omit for the first page.</param>
    /// <param name="preview">Include the text preview (default true); false is cheaper for the server.</param>
    /// <param name="order"><c>newest</c> (default) or <c>oldest</c>: by arrival.</param>
    private static async Task<IResult> ListAsync(
        string folder,
        HttpContext http,
        IMailGateway gateway,
        SessionService sessions,
        CancellationToken cancellationToken,
        [FromQuery] int? limit = null,
        [FromQuery] string? after = null,
        [FromQuery] string? order = null,
        [FromQuery] bool? preview = null)
    {
        var pageSize = limit ?? MessageListQuery.DefaultLimit;
        if (pageSize is < 1 or > MessageListQuery.MaxLimit)
        {
            return Bad($"limit must be between 1 and {MessageListQuery.MaxLimit}.");
        }

        MessageCursor? cursor = null;
        if (!string.IsNullOrEmpty(after))
        {
            if (!MessageCursor.TryParse(after, out var parsed))
            {
                return Bad("after is not a valid cursor.");
            }

            cursor = parsed;
        }

        if (order is not (null or "newest" or "oldest"))
        {
            return Bad("order must be newest or oldest.");
        }

        var query = new MessageListQuery(folder, cursor, pageSize, order != "oldest", preview ?? true);
        var page = await gateway.ExecuteAsync(
            http.GetMailCredentials(sessions),
            (session, token) => session.ListMessagesAsync(query, token),
            cancellationToken);

        return Results.Ok(new MessageListResponse([.. page.Items.Select(ToDto)], page.Next?.ToString(), page.Total));
    }

    private static IResult Bad(string title) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title, extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.BadRequest });

    private static MessageListItemDto ToDto(MessageListItem item) => new(
        item.Id.ToString(),
        item.From is null ? null : new AddressDto(item.From.Name, item.From.Address),
        [.. item.To.Select(a => new AddressDto(a.Name, a.Address))],
        item.Subject,
        item.Date,
        item.Seen,
        item.Flagged,
        item.Answered,
        item.Draft,
        item.HasAttachments,
        item.Labels,
        item.Size,
        item.Preview);
}
