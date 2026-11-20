using Vinette.Api.Errors;
using Vinette.Api.Sessions;
using Vinette.Core.Mail;

namespace Vinette.Api.Mail;

/// <summary>The people to offer while a recipient is typed, best first.</summary>
/// <param name="Items">At most the number asked for.</param>
public sealed record RecipientSuggestionsResponse(IReadOnlyList<AddressDto> Items);

/// <summary>What is known of one address.</summary>
/// <param name="Address">The address, as asked.</param>
/// <param name="Known">The mailbox has mail with this person (among the newest it looks at).</param>
/// <param name="LastMail">When the newest message with this person is dated; absent if not known.</param>
public sealed record RecipientLookupItem(string Address, bool Known, DateTimeOffset? LastMail);

/// <summary>What is known of the addresses asked about, in the order asked.</summary>
/// <param name="Items">One for each address, repeats left out.</param>
public sealed record RecipientLookupResponse(IReadOnlyList<RecipientLookupItem> Items);

/// <summary>Suggestions of recipients (VIN-095): <c>GET /recipients/suggestions?q=&amp;limit=</c>; what is known of the people of a message (VIN-105): <c>GET /recipients/lookup?address=</c>.</summary>
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
        api.MapGet("/recipients/lookup", LookupAsync)
            .WithName("LookupRecipients")
            .WithTags("Recipients")
            .Produces<RecipientLookupResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return api;
    }

    /// <summary>The most addresses one lookup may ask about.</summary>
    public const int MaxLookupAddresses = 100;

    /// <summary>
    /// For each <c>address</c> (repeat the parameter; at most 100, each 1 to 254 characters), whether the mailbox deals with that person and when the
    /// newest message with them is dated: for the "Recipients" card of the editor. Learned from the same newest mail as the suggestions.
    /// </summary>
    private static async Task<IResult> LookupAsync(string[] address, HttpContext http, SessionService sessions, IRecipientSuggestions suggestions, CancellationToken cancellationToken)
    {
        var asked = address.Select(a => a.Trim()).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (asked.Count > MaxLookupAddresses)
        {
            return Problem($"At most {MaxLookupAddresses} addresses can be asked about at once.");
        }

        if (asked.Any(a => a.Length > 254))
        {
            return Problem("An address can have at most 254 characters.");
        }

        if (asked.Count == 0)
        {
            return Results.Ok(new RecipientLookupResponse([]));
        }

        var found = await suggestions.LookupAsync(http.GetMailCredentials(sessions), asked, cancellationToken);
        return Results.Ok(new RecipientLookupResponse([.. found.Select(k => new RecipientLookupItem(k.Address, k.LastMail is not null, k.LastMail))]));
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
