using Vinette.Api.Errors;
using Vinette.Core.Mail;

namespace Vinette.Api.Mail;

/// <summary>The HTML of a message that is sent, kept as a draft or put off (VIN-108): always cleaned on the server, whatever the client sent.</summary>
internal static class OutgoingHtml
{
    /// <summary>
    /// Cleans <paramref name="html"/> with the sanitizer of the application (the editor's vocabulary only: <see cref="IMessageHtmlSanitizer.SanitizeOutgoing"/>).
    /// Returns null and the clean HTML (null if there is none), or the answer to give when the markup is refused: nested too deeply or too long
    /// (a message is never cut), 400 <c>bad_request</c>.
    /// </summary>
    public static IResult? Clean(string? html, IMessageHtmlSanitizer sanitizer, out string? clean)
    {
        var result = sanitizer.SanitizeOutgoing(html);
        if (result.Rejected || result.Truncated)
        {
            clean = null;
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The formatted text of the message is too long or too deeply nested.",
                extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.BadRequest });
        }

        clean = string.IsNullOrWhiteSpace(result.Html) ? null : result.Html;
        return null;
    }
}
