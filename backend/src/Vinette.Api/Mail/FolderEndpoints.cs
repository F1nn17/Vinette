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
        return api;
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
        return new FoldersResponse([.. FolderOrder.Sort(folders).Select(ToDto)]);
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
