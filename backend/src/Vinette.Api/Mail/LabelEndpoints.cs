using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Vinette.Api.Errors;
using Vinette.Api.Sessions;
using Vinette.Core.Mail;
using Vinette.Core.Settings;

namespace Vinette.Api.Mail;

/// <summary>A label.</summary>
/// <param name="Name">The name the user sees.</param>
/// <param name="Color">A key of the design's palette (<c>work</c>, <c>bills</c>, <c>personal</c>); absent if the label has no chosen color.</param>
public sealed record LabelDto(string Name, string? Color);

/// <summary>All the labels of the mailbox, by name.</summary>
public sealed record LabelsResponse(IReadOnlyList<LabelDto> Labels);

/// <summary>A new label.</summary>
/// <param name="Name">The name; up to about 50 letters of the Latin alphabet or 14 of any other (the mail server keeps it in a short code).</param>
/// <param name="Color">A key of the palette; absent for none.</param>
public sealed record CreateLabelRequest(string? Name, string? Color);

/// <summary>A change of a label: only the members that are present change.</summary>
/// <param name="Name">The new name; the label is renamed on every message that has it.</param>
/// <param name="Color">A key of the palette; an empty text takes the color away.</param>
public sealed record UpdateLabelRequest(string? Name, string? Color);

/// <summary>Labels to put on a message and to take off it.</summary>
/// <param name="Add">Names to put on (created if they are new).</param>
/// <param name="Remove">Names to take off.</param>
public sealed record MessageLabelsRequest(IReadOnlyList<string>? Add, IReadOnlyList<string>? Remove);

/// <summary>The labels a message has.</summary>
public sealed record MessageLabelsDto(IReadOnlyList<string> Labels);

/// <summary>
/// Labels are IMAP keywords on the messages, so that other mail programs see them too (<see cref="LabelName"/> says how a name
/// is kept). What the server cannot keep is kept in the user's settings: the list of labels (a label can exist before any message
/// has it) and the colors.
/// </summary>
public static class LabelEndpoints
{
    /// <summary>The most labels put on or taken off one message in one request.</summary>
    public const int MaxChangesPerRequest = 20;

    public static IEndpointRouteBuilder MapLabels(this IEndpointRouteBuilder api)
    {
        api.MapGet("/labels", GetAsync)
            .WithName("GetLabels")
            .WithTags("Labels")
            .Produces<LabelsResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        api.MapPost("/labels", CreateAsync)
            .WithName("CreateLabel")
            .WithTags("Labels")
            .Produces<LabelDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        api.MapPatch("/labels/{name}", UpdateAsync)
            .WithName("UpdateLabel")
            .WithTags("Labels")
            .Produces<LabelDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        api.MapDelete("/labels/{name}", DeleteAsync)
            .WithName("DeleteLabel")
            .WithTags("Labels")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        api.MapGet("/labels/{name}/messages", ListMessagesAsync)
            .WithName("ListLabeledMessages")
            .WithTags("Labels")
            .Produces<MessageListResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        api.MapPatch("/messages/{id}/labels", UpdateMessageLabelsAsync)
            .WithName("UpdateMessageLabels")
            .WithTags("Mail")
            .Produces<MessageLabelsDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return api;
    }

    /// <summary>The labels of the mailbox: the user's own list plus those the mail server knows from the messages (put there by other programs too).</summary>
    private static async Task<IResult> GetAsync(HttpContext http, ClaimsPrincipal user, IMailGateway gateway, SessionService sessions, IUserSettingsStore store, CancellationToken cancellationToken)
    {
        var known = await gateway.ExecuteAsync(http.GetMailCredentials(sessions), (session, token) => session.GetLabelsAsync(token), cancellationToken);
        var settings = await store.GetAsync(user.Identity!.Name!, cancellationToken);
        return Results.Ok(new LabelsResponse(All(settings, known)));
    }

    private static List<LabelDto> All(UserSettings settings, IEnumerable<string> known)
    {
        var names = new List<string>();
        foreach (var name in settings.Labels.Concat(known))
        {
            if (!names.Any(n => LabelName.Same(n, name)))
            {
                names.Add(name);
            }
        }

        return [.. names.Order(StringComparer.CurrentCultureIgnoreCase).Select(n => new LabelDto(n, settings.ColorOf(n)))];
    }

    private static async Task<IResult> CreateAsync(CreateLabelRequest? request, HttpContext http, ClaimsPrincipal user, IMailGateway gateway, SessionService sessions, IUserSettingsStore store, CancellationToken cancellationToken)
    {
        if (Check(request?.Name, request?.Color, nameRequired: true) is { } bad)
        {
            return bad;
        }

        var name = LabelName.Normalize(request!.Name!);
        var known = await gateway.ExecuteAsync(http.GetMailCredentials(sessions), (session, token) => session.GetLabelsAsync(token), cancellationToken);
        var created = false;
        UserSettings saved;
        try
        {
            saved = await store.UpdateAsync(
                user.Identity!.Name!,
                current =>
                {
                    if (All(current, known).Any(l => LabelName.Same(l.Name, name)))
                    {
                        return current;
                    }

                    created = true;
                    return current.WithLabel(name, request.Color ?? string.Empty);
                },
                cancellationToken);
        }
        catch (InvalidSettingsException exception)
        {
            return Problem(StatusCodes.Status400BadRequest, exception.Message, ErrorCodes.BadRequest);
        }

        return created
            ? Results.Created($"/api/v1/labels/{Uri.EscapeDataString(name)}", new LabelDto(name, saved.ColorOf(name)))
            : Problem(StatusCodes.Status409Conflict, "A label with this name already exists.", ErrorCodes.Conflict);
    }

    /// <summary>Renames a label (on every message that has it, in every folder) and/or changes its color.</summary>
    /// <param name="name">The label's name.</param>
    private static async Task<IResult> UpdateAsync(string name, UpdateLabelRequest? request, HttpContext http, ClaimsPrincipal user, IMailGateway gateway, SessionService sessions, IUserSettingsStore store, CancellationToken cancellationToken)
    {
        if (request is null || (request.Name is null && request.Color is null))
        {
            return Problem(StatusCodes.Status400BadRequest, "The body must say what to change: name, color or both.", ErrorCodes.BadRequest);
        }

        if (Check(request.Name, request.Color, nameRequired: false) is { } bad)
        {
            return bad;
        }

        var credentials = http.GetMailCredentials(sessions);
        var known = await gateway.ExecuteAsync(credentials, (session, token) => session.GetLabelsAsync(token), cancellationToken);
        var settings = await store.GetAsync(user.Identity!.Name!, cancellationToken);
        var existing = All(settings, known).FirstOrDefault(l => LabelName.Same(l.Name, name));
        if (existing is null)
        {
            return Problem(StatusCodes.Status404NotFound, "There is no such label.", ErrorCodes.NotFound);
        }

        var newName = request.Name is null ? existing.Name : LabelName.Normalize(request.Name);
        var renamed = !string.Equals(newName, existing.Name, StringComparison.Ordinal);
        if (renamed && !LabelName.Same(newName, existing.Name) && All(settings, known).Any(l => LabelName.Same(l.Name, newName)))
        {
            return Problem(StatusCodes.Status409Conflict, "A label with this name already exists.", ErrorCodes.Conflict);
        }

        if (renamed)
        {
            // On the server first: if it fails, the settings still say the old name.
            _ = await gateway.ExecuteAsync(credentials, (session, token) => session.RenameLabelAsync(existing.Name, newName, token), cancellationToken);
        }

        try
        {
            var saved = await store.UpdateAsync(
                user.Identity!.Name!,
                current =>
                {
                    var changed = renamed ? current.WithRenamedLabel(existing.Name, newName) : current.WithLabel(existing.Name);
                    return request.Color is null ? changed : changed.WithLabel(newName, request.Color);
                },
                cancellationToken);
            return Results.Ok(new LabelDto(newName, saved.ColorOf(newName)));
        }
        catch (InvalidSettingsException exception)
        {
            return Problem(StatusCodes.Status400BadRequest, exception.Message, ErrorCodes.BadRequest);
        }
    }

    /// <summary>Deletes a label: it is taken off every message that has it and forgotten. The messages themselves are not touched.</summary>
    /// <param name="name">The label's name.</param>
    private static async Task<IResult> DeleteAsync(string name, HttpContext http, ClaimsPrincipal user, IMailGateway gateway, SessionService sessions, IUserSettingsStore store, CancellationToken cancellationToken)
    {
        var credentials = http.GetMailCredentials(sessions);
        var known = await gateway.ExecuteAsync(credentials, (session, token) => session.GetLabelsAsync(token), cancellationToken);
        var settings = await store.GetAsync(user.Identity!.Name!, cancellationToken);
        var existing = All(settings, known).FirstOrDefault(l => LabelName.Same(l.Name, name));
        if (existing is null)
        {
            return Problem(StatusCodes.Status404NotFound, "There is no such label.", ErrorCodes.NotFound);
        }

        _ = await gateway.ExecuteAsync(credentials, (session, token) => session.RenameLabelAsync(existing.Name, null, token), cancellationToken);
        _ = await store.UpdateAsync(user.Identity!.Name!, current => current.WithoutLabel(existing.Name), cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Puts labels on a message and takes labels off it; a name that is new becomes a label of the mailbox.</summary>
    /// <param name="id">The message's identifier from the list.</param>
    private static async Task<IResult> UpdateMessageLabelsAsync(MessageId id, MessageLabelsRequest? request, HttpContext http, ClaimsPrincipal user, IMailGateway gateway, SessionService sessions, IUserSettingsStore store, CancellationToken cancellationToken)
    {
        var add = (request?.Add ?? []).Select(LabelName.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var remove = (request?.Remove ?? []).Select(LabelName.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (add.Count + remove.Count == 0 || add.Count + remove.Count > MaxChangesPerRequest)
        {
            return Problem(StatusCodes.Status400BadRequest, $"The body must name 1 to {MaxChangesPerRequest} labels to add or remove.", ErrorCodes.BadRequest);
        }

        foreach (var name in add.Concat(remove))
        {
            if (!LabelName.IsValid(name, out var problem))
            {
                return Problem(StatusCodes.Status400BadRequest, problem!, ErrorCodes.BadRequest);
            }
        }

        if (add.Any(a => remove.Any(r => LabelName.Same(a, r))))
        {
            return Problem(StatusCodes.Status400BadRequest, "A label cannot be added and removed at once.", ErrorCodes.BadRequest);
        }

        var labels = await gateway.ExecuteAsync(
            http.GetMailCredentials(sessions),
            (session, token) => session.UpdateMessageLabelsAsync(id, add, remove, token),
            cancellationToken);

        if (add.Count > 0)
        {
            try
            {
                _ = await store.UpdateAsync(user.Identity!.Name!, current => add.Aggregate(current, (s, n) => s.Labels.Any(l => LabelName.Same(l, n)) ? s : s.WithLabel(n)), cancellationToken);
            }
            catch (InvalidSettingsException)
            {
                // Too many labels to list: the message has its label all the same, it is just not in the user's list.
            }
        }

        return Results.Ok(new MessageLabelsDto(labels));
    }

    /// <summary>
    /// The newest messages with a label from all folders except the trash and the spam folder, newest first. There is one page only:
    /// <c>next</c> is always absent, and <c>total</c> says how many messages have the label (more than are listed if it is above <c>limit</c>).
    /// A label nobody has put on a message yields an empty list.
    /// </summary>
    /// <param name="name">The label's name (URL-encoded).</param>
    /// <param name="limit">Messages to list, 1 to 100 (default 50).</param>
    /// <param name="preview">Include the text preview (default true).</param>
    private static async Task<IResult> ListMessagesAsync(string name, HttpContext http, IMailGateway gateway, SessionService sessions, CancellationToken cancellationToken, [FromQuery] int? limit = null, [FromQuery] bool? preview = null)
    {
        var pageSize = limit ?? MessageListQuery.DefaultLimit;
        if (pageSize is < 1 or > MessageListQuery.MaxLimit)
        {
            return Problem(StatusCodes.Status400BadRequest, $"limit must be between 1 and {MessageListQuery.MaxLimit}.", ErrorCodes.BadRequest);
        }

        if (!LabelName.IsValid(name, out var problem))
        {
            return Problem(StatusCodes.Status400BadRequest, problem!, ErrorCodes.BadRequest);
        }

        var found = await gateway.ExecuteAsync(
            http.GetMailCredentials(sessions),
            (session, token) => session.ListLabeledMessagesAsync(LabelName.Normalize(name), pageSize, preview ?? true, token),
            cancellationToken);
        return Results.Ok(new MessageListResponse([.. found.Items.Select(MessageListEndpoints.ToDto)], null, found.Total));
    }

    private static IResult? Check(string? name, string? color, bool nameRequired)
    {
        if ((nameRequired || name is not null) && !LabelName.IsValid(name, out var problem))
        {
            return Problem(StatusCodes.Status400BadRequest, problem!, ErrorCodes.BadRequest);
        }

        if (!string.IsNullOrEmpty(color) && !UserSettings.LabelPalette.Contains(color, StringComparer.Ordinal))
        {
            return Problem(StatusCodes.Status400BadRequest, $"The color must be one of: {string.Join(", ", UserSettings.LabelPalette)}.", ErrorCodes.BadRequest);
        }

        return null;
    }

    private static IResult Problem(int status, string title, string code) =>
        Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });
}
