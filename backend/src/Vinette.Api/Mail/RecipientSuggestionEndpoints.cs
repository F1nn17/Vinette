using Vinette.Api.Errors;
using Vinette.Api.Sessions;
using Vinette.Core.Mail;

namespace Vinette.Api.Mail;

/// <summary>The people to offer while a recipient is typed, best first.</summary>
/// <param name="Items">At most the number asked for.</param>
public sealed record RecipientSuggestionsResponse(IReadOnlyList<AddressDto> Items);

/// <summary>Suggestions of recipients (VIN-095): <c>GET /recipients/suggestions?q=&amp;limit=</c>.</summary>
public static class RecipientSuggestionEndpoints
{
    public static IEndpointRouteBuilder MapRecipientSuggestions(this IEndpointRouteBuilder api)
    {
        api.MapGet("/recipients/suggestions", SuggestAsync)
            .WithName("SuggestRecipients")
            .WithTags("Recipients")
            .Produces<RecipientSuggestionsResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return api;
    }

    /// <summary>
    /// The people the mailbox writes to and hears from that match what was typed (<c>q</c>: the beginning of an address, a name or a word of a
    /// name; empty gives the most frequent ones). <c>limit</c>: 1 to 20, 8 if absent. The first answer for a mailbox reads the newest mail
    /// (a few seconds at most); the following ones come from memory.
    /// </summary>
    private static async Task<IResult> SuggestAsync(string? q, int? limit, HttpContext http, SessionService sessions, IRecipientSuggestions suggestions, CancellationToken cancellationToken)
    {
        var count = limit ?? SuggestionLimits.DefaultCount;
        if (count is < 1 or > SuggestionLimits.MaxCount)
        {
            return Problem($"limit must be between 1 and {SuggestionLimits.MaxCount}.");
        }

        if ((q?.Length ?? 0) > SuggestionLimits.MaxQueryLength)
        {
            return Problem($"q can have at most {SuggestionLimits.MaxQueryLength} characters.");
        }

        var found = await suggestions.SuggestAsync(http.GetMailCredentials(sessions), q ?? string.Empty, count, cancellationToken);
        return Results.Ok(new RecipientSuggestionsResponse([.. found.Select(a => new AddressDto(a.Name, a.Address))]));
    }

    private static IResult Problem(string title) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title, extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.BadRequest });
}
