using Vinette.Api.Errors;
using Vinette.Api.Sessions;
using Vinette.Core.Mail;

namespace Vinette.Api.Mail;

/// <summary>One action on several messages.</summary>
/// <param name="Ids">The messages, 1 to 100, all of one folder (identifiers from the list).</param>
/// <param name="Action"><c>archive</c>, <c>spam</c>, <c>trash</c>, <c>move</c> (with <c>to</c>), <c>read</c>, <c>unread</c>, <c>flag</c> or <c>unflag</c>.</param>
/// <param name="To">For <c>move</c>: the folder's <c>path</c>.</param>
public sealed record MessageBatchRequest(IReadOnlyList<string>? Ids, string? Action, string? To);

/// <summary>A message of the batch after it was moved.</summary>
/// <param name="From">Its identifier before.</param>
/// <param name="Id">Its identifier in the new folder; absent if the mail server does not say it.</param>
public sealed record BatchMovedDto(string From, string? Id);

/// <summary>What the batch did.</summary>
/// <param name="Changed">How many messages were changed; fewer than asked if some were no longer there.</param>
/// <param name="Folder">For a move: the folder the messages are in now.</param>
/// <param name="Moved">For a move: each moved message with its new identifier, so that the move can be undone.</param>
public sealed record MessageBatchResultDto(int Changed, string? Folder, IReadOnlyList<BatchMovedDto> Moved);

/// <summary>
/// Actions on the messages selected in the list (VIN-073). One request takes at most <see cref="MessageBatch.MaxMessages"/>
/// messages of one folder and is one command to the mail server per change (a set of UIDs), so a batch is as quick as one message.
/// </summary>
public static class MessageBatchEndpoints
{
    private static readonly Dictionary<string, FolderRole> RoleActions = new(StringComparer.Ordinal)
    {
        ["archive"] = FolderRole.Archive,
        ["spam"] = FolderRole.Junk,
        ["trash"] = FolderRole.Trash,
    };

    private static readonly Dictionary<string, MessageFlagChange> FlagActions = new(StringComparer.Ordinal)
    {
        ["read"] = new MessageFlagChange(true, null),
        ["unread"] = new MessageFlagChange(false, null),
        ["flag"] = new MessageFlagChange(null, true),
        ["unflag"] = new MessageFlagChange(null, false),
    };

    public static IEndpointRouteBuilder MapMessageBatch(this IEndpointRouteBuilder api)
    {
        api.MapPost("/messages/batch", BatchAsync)
            .WithName("ChangeMessages")
            .WithTags("Mail")
            .Produces<MessageBatchResultDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return api;
    }

    /// <summary>Archives, marks as spam, trashes, moves or marks several messages of one folder at once.</summary>
    private static async Task<IResult> BatchAsync(MessageBatchRequest? request, HttpContext http, IMailGateway gateway, SessionService sessions, CancellationToken cancellationToken)
    {
        var ids = new List<MessageId>();
        foreach (var text in request?.Ids ?? [])
        {
            if (!MessageId.TryParse(text, null, out var id))
            {
                return Problem(StatusCodes.Status400BadRequest, "An id is not a message id.", ErrorCodes.BadRequest);
            }

            ids.Add(id);
        }

        if (!MessageBatch.IsValid(ids, out var problem))
        {
            return Problem(StatusCodes.Status400BadRequest, problem!, ErrorCodes.BadRequest);
        }

        var action = request!.Action ?? string.Empty;
        var credentials = http.GetMailCredentials(sessions);

        if (FlagActions.TryGetValue(action, out var change))
        {
            var changed = await gateway.ExecuteAsync(credentials, (session, token) => session.UpdateFlagsOfManyAsync(ids, change, token), cancellationToken);
            return Results.Ok(new MessageBatchResultDto(changed.Count, null, []));
        }

        if (action == "move")
        {
            if (string.IsNullOrWhiteSpace(request.To) || request.To.Length > 1024 || request.To.Any(char.IsControl))
            {
                return Problem(StatusCodes.Status400BadRequest, "A move must say the folder to move to (to).", ErrorCodes.BadRequest);
            }

            if (SameFolder(ids[0].Folder, request.To))
            {
                return Problem(StatusCodes.Status400BadRequest, "The messages are already in that folder.", ErrorCodes.BadRequest);
            }

            var target = request.To;
            var moved = await gateway.ExecuteAsync(credentials, (session, token) => session.MoveMessagesAsync(ids, target, token), cancellationToken);
            return Results.Ok(Result(moved, target));
        }

        if (!RoleActions.TryGetValue(action, out var role))
        {
            return Problem(StatusCodes.Status400BadRequest, "action must be archive, spam, trash, move, read, unread, flag or unflag.", ErrorCodes.BadRequest);
        }

        var (folder, result) = await gateway.ExecuteAsync(
            credentials,
            async (session, token) =>
            {
                var folders = FolderRoleGuesser.Complete(await session.GetFoldersAsync(token)); // the same roles as the sidebar shows
                var to = folders.FirstOrDefault(f => f.Role == role && f.CanContainMessages);
                if (to is null || SameFolder(ids[0].Folder, to.Path))
                {
                    return (to?.Path, (IReadOnlyList<MovedMessage>?)null);
                }

                return (to.Path, await session.MoveMessagesAsync(ids, to.Path, token));
            },
            cancellationToken);

        return (folder, result) switch
        {
            (null, _) => Problem(StatusCodes.Status409Conflict, "The mailbox has no folder for this action.", ErrorCodes.NoTargetFolder),
            (_, null) => Problem(StatusCodes.Status400BadRequest, "The messages are already in that folder.", ErrorCodes.BadRequest),
            _ => Results.Ok(Result(result, folder)),
        };
    }

    private static MessageBatchResultDto Result(IReadOnlyList<MovedMessage> moved, string folder) =>
        new(moved.Count, folder, [.. moved.Select(m => new BatchMovedDto(m.From.ToString(), m.To?.ToString()))]);

    private static bool SameFolder(string a, string b) =>
        string.Equals(a, b, StringComparison.Ordinal) || (IsInbox(a) && IsInbox(b));

    private static bool IsInbox(string path) => string.Equals(path, "INBOX", StringComparison.OrdinalIgnoreCase);

    private static IResult Problem(int status, string title, string code) =>
        Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });
}
