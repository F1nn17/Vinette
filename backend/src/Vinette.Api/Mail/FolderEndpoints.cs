using Vinette.Api.Errors;
using Vinette.Api.Sessions;
using Vinette.Core.Mail;

namespace Vinette.Api.Mail;

/// <summary>One folder as the client sees it. <c>path</c> is the folder's identifier in all routes.</summary>
/// <param name="Path">Full folder name with the server's separators, e.g. "Work/Reports"; "INBOX" for the inbox.</param>
/// <param name="Name">The last part of the path, for display (special folders get localized names on the client).</param>
/// <param name="ParentPath">The folder this one is inside; absent at the top level.</param>
/// <param name="Role">inbox, drafts, sent, archive, junk, trash or other.</param>
/// <param name="CanContainMessages">False for folders that only group others.</param>
/// <param name="Total">Messages in the folder.</param>
/// <param name="Unread">Unread messages in the folder.</param>
/// <param name="Subscribed">The folder is subscribed (shown by default).</param>
public sealed record FolderDto(
    string Path,
    string Name,
    string? ParentPath,
    string Role,
    bool CanContainMessages,
    int Total,
    int Unread,
    bool Subscribed);

/// <summary>Folders in sidebar order: special folders first, children right after their parent.</summary>
public sealed record FoldersResponse(IReadOnlyList<FolderDto> Folders);

/// <summary>Storage use of the mailbox, in bytes.</summary>
public sealed record StorageDto(long UsedBytes, long LimitBytes);

/// <summary>Mailbox quota. <c>storage</c> is absent when the server does not report one: show nothing then.</summary>
public sealed record QuotaResponse(StorageDto? Storage);

/// <summary>What emptying a folder did.</summary>
/// <param name="Deleted">How many messages were deleted for good.</param>
public sealed record EmptyFolderResult(int Deleted);

public static class FolderEndpoints
{
    public static IEndpointRouteBuilder MapFolders(this IEndpointRouteBuilder api)
    {
        api.MapGet("/folders", GetFoldersAsync)
            .WithName("GetFolders")
            .WithTags("Mail")
            .Produces<FoldersResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        api.MapPost("/folders/{folder}/empty", EmptyAsync)
            .WithName("EmptyFolder")
            .WithTags("Mail")
            .Produces<EmptyFolderResult>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        api.MapGet("/quota", GetQuotaAsync)
            .WithName("GetQuota")
            .WithTags("Mail")
            .Produces<QuotaResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return api;
    }

    /// <summary>
    /// Deletes every message of the trash or of the spam folder for good (there is no way back). Any other folder: 409 <c>not_in_trash</c>.
    /// </summary>
    /// <param name="folder">The folder's path (URL-encoded, "/" as %2F).</param>
    private static async Task<IResult> EmptyAsync(string folder, HttpContext http, IMailGateway gateway, SessionService sessions, CancellationToken cancellationToken)
    {
        var deleted = await gateway.ExecuteAsync(
            http.GetMailCredentials(sessions),
            async (session, token) =>
            {
                var folders = FolderRoleGuesser.Complete(await session.GetFoldersAsync(token)); // the same roles as the sidebar shows
                return folders.Any(f => f.Role is FolderRole.Trash or FolderRole.Junk && f.CanContainMessages && string.Equals(f.Path, folder, StringComparison.Ordinal))
                    ? await session.EmptyFolderAsync(folder, token)
                    : (int?)null;
            },
            cancellationToken);
        return deleted is null
            ? Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Only the trash and the spam folder can be emptied.",
                extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.NotInTrash })
            : Results.Ok(new EmptyFolderResult(deleted.Value));
    }

    private static async Task<QuotaResponse> GetQuotaAsync(
        HttpContext http,
        IMailGateway gateway,
        SessionService sessions,
        CancellationToken cancellationToken)
    {
        var quota = await gateway.ExecuteAsync(
            http.GetMailCredentials(sessions),
            (session, token) => session.GetQuotaAsync(token),
            cancellationToken);
        return new QuotaResponse(quota is null ? null : new StorageDto(quota.UsedBytes, quota.LimitBytes));
    }

    private static async Task<FoldersResponse> GetFoldersAsync(
        HttpContext http,
        IMailGateway gateway,
        SessionService sessions,
        CancellationToken cancellationToken)
    {
        var folders = await gateway.ExecuteAsync(
            http.GetMailCredentials(sessions),
            (session, token) => session.GetFoldersAsync(token),
            cancellationToken);
        return new FoldersResponse([.. FolderOrder.Sort(FolderRoleGuesser.Complete(folders)).Select(ToDto)]);
    }

    private static FolderDto ToDto(MailFolderInfo folder) => new(
        folder.Path,
        folder.Name,
        folder.ParentPath,
        folder.Role.ToString().ToLowerInvariant(),
        folder.CanContainMessages,
        folder.Total,
        folder.Unread,
        folder.Subscribed);
}
