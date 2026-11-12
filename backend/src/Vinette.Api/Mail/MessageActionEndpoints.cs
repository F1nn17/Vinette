using Vinette.Api.Errors;
using Vinette.Api.Sessions;
using Vinette.Core.Mail;

namespace Vinette.Api.Mail;

/// <summary>Where to move a message.</summary>
/// <param name="To">The folder's <c>path</c> from the folder list.</param>
public sealed record MoveMessageRequest(string? To);

/// <summary>A message after it was moved.</summary>
/// <param name="Id">The message's identifier in the new folder; absent if the mail server does not say what it is (look for the message in the folder).</param>
/// <param name="Folder">The folder the message is in now (its <c>path</c>).</param>
public sealed record MovedMessageDto(string? Id, string Folder);

/// <summary>
/// What can be done with a message besides reading it: archive, mark as spam, move to the trash or to any folder, and delete
/// for good from the trash. Everything except the last is reversible (the message is only in another folder, and the
/// answer says where, so that the client can move it back).
/// </summary>
public static class MessageActionEndpoints
{
    private enum Outcome
    {
        Moved,
        NoSuchFolderForRole,
        AlreadyThere,
        NotInTrash,
        Deleted,
    }

    public static IEndpointRouteBuilder MapMessageActions(this IEndpointRouteBuilder api)
    {
        MapMove(api, "/messages/{id}/archive", "ArchiveMessage", FolderRole.Archive);
        MapMove(api, "/messages/{id}/spam", "MarkMessageAsSpam", FolderRole.Junk);
        MapMove(api, "/messages/{id}/trash", "TrashMessage", FolderRole.Trash);
        api.MapPost("/messages/{id}/move", MoveAsync)
            .WithName("MoveMessage")
            .WithTags("Mail")
            .Produces<MovedMessageDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        api.MapDelete("/messages/{id}", DeleteAsync)
            .WithName("DeleteMessage")
            .WithTags("Mail")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return api;
    }

    private static void MapMove(IEndpointRouteBuilder api, string pattern, string name, FolderRole role) =>
        api.MapPost(
                pattern,
                (MessageId id, HttpContext http, IMailGateway gateway, SessionService sessions, CancellationToken cancellationToken) =>
                    MoveToRoleAsync(id, role, http, gateway, sessions, cancellationToken))
            .WithName(name)
            .WithTags("Mail")
            .Produces<MovedMessageDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

    /// <summary>Moves a message to the mailbox's folder of this kind (archive, spam, trash); 409 <c>no_target_folder</c> if the mailbox has none.</summary>
    private static async Task<IResult> MoveToRoleAsync(MessageId id, FolderRole role, HttpContext http, IMailGateway gateway, SessionService sessions, CancellationToken cancellationToken)
    {
        var (outcome, folder, moved) = await gateway.ExecuteAsync(
            http.GetMailCredentials(sessions),
            async (session, token) =>
            {
                var folders = FolderRoleGuesser.Complete(await session.GetFoldersAsync(token)); // the same roles as the sidebar shows
                var target = folders.FirstOrDefault(f => f.Role == role && f.CanContainMessages);
                if (target is null)
                {
                    return (Outcome.NoSuchFolderForRole, string.Empty, null);
                }

                return SameFolder(id.Folder, target.Path)
                    ? (Outcome.AlreadyThere, target.Path, null)
                    : (Outcome.Moved, target.Path, await session.MoveMessageAsync(id, target.Path, token));
            },
            cancellationToken);

        return Answer(outcome, folder, moved);
    }

    /// <summary>Moves a message to a folder the client names.</summary>
    /// <param name="id">The message's identifier from the list.</param>
    private static async Task<IResult> MoveAsync(MessageId id, MoveMessageRequest? request, HttpContext http, IMailGateway gateway, SessionService sessions, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.To) || request.To.Length > 1024 || request.To.Any(char.IsControl))
        {
            return Problem(StatusCodes.Status400BadRequest, "The body must say the folder to move to: {\"to\": \"<folder path>\"}.", ErrorCodes.BadRequest);
        }

        if (SameFolder(id.Folder, request.To))
        {
            return Answer(Outcome.AlreadyThere, request.To, null);
        }

        var target = request.To;
        var moved = await gateway.ExecuteAsync(
            http.GetMailCredentials(sessions),
            (session, token) => session.MoveMessageAsync(id, target, token),
            cancellationToken);
        return Answer(Outcome.Moved, target, moved);
    }

    /// <summary>
    /// Deletes a message for good. Allowed only for a message in the trash (409 <c>not_in_trash</c> otherwise): anything else is
    /// first moved to the trash, from where it can still be brought back.
    /// </summary>
    /// <param name="id">The message's identifier from the list.</param>
    private static async Task<IResult> DeleteAsync(MessageId id, HttpContext http, IMailGateway gateway, SessionService sessions, CancellationToken cancellationToken)
    {
        var outcome = await gateway.ExecuteAsync(
            http.GetMailCredentials(sessions),
            async (session, token) =>
            {
                var folders = FolderRoleGuesser.Complete(await session.GetFoldersAsync(token)); // the same roles as the sidebar shows
                if (!folders.Any(f => f.Role == FolderRole.Trash && SameFolder(f.Path, id.Folder)))
                {
                    return Outcome.NotInTrash;
                }

                await session.DeleteMessagePermanentlyAsync(id, token);
                return Outcome.Deleted;
            },
            cancellationToken);

        return outcome == Outcome.Deleted
            ? Results.NoContent()
            : Problem(StatusCodes.Status409Conflict, "Only a message in the trash can be deleted for good; move it to the trash first.", ErrorCodes.NotInTrash);
    }

    private static IResult Answer(Outcome outcome, string folder, MessageId? moved) => outcome switch
    {
        Outcome.Moved => Results.Ok(new MovedMessageDto(moved?.ToString(), folder)),
        Outcome.NoSuchFolderForRole => Problem(StatusCodes.Status409Conflict, "The mailbox has no folder for this action.", ErrorCodes.NoTargetFolder),
        _ => Problem(StatusCodes.Status400BadRequest, "The message is already in that folder.", ErrorCodes.BadRequest),
    };

    private static bool SameFolder(string a, string b) =>
        string.Equals(a, b, StringComparison.Ordinal) || (IsInbox(a) && IsInbox(b));

    private static bool IsInbox(string path) => string.Equals(path, "INBOX", StringComparison.OrdinalIgnoreCase);

    private static IResult Problem(int status, string title, string code) =>
        Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });
}
