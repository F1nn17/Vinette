namespace Vinette.Api.Errors;

/// <summary>
/// Catalog of machine-readable error codes returned in the "code" member of ProblemDetails.
/// The frontend maps codes to user-facing texts; keep docs/api-errors.md in sync.
/// </summary>
public static class ErrorCodes
{
    public const string BadRequest = "bad_request";
    public const string ValidationFailed = "validation_failed";
    public const string Unauthorized = "unauthorized";
    public const string InvalidCredentials = "invalid_credentials";
    public const string SessionRevoked = "session_revoked";
    public const string MailUnavailable = "mail_unavailable";
    public const string Forbidden = "forbidden";
    public const string CsrfFailed = "csrf_failed";
    public const string OriginNotAllowed = "origin_not_allowed";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    public const string PayloadTooLarge = "payload_too_large";
    public const string TooManyRequests = "too_many_requests";
    public const string InternalError = "internal_error";
    public const string ServiceUnavailable = "service_unavailable";

    /// <summary>Prefix of the ProblemDetails "type" URI.</summary>
    public const string TypePrefix = "urn:vinette:error:";

    /// <summary>Default code for an HTTP status when a handler did not set a specific one.</summary>
    public static string ForStatus(int status) => status switch
    {
        StatusCodes.Status400BadRequest => BadRequest,
        StatusCodes.Status401Unauthorized => Unauthorized,
        StatusCodes.Status403Forbidden => Forbidden,
        StatusCodes.Status404NotFound => NotFound,
        StatusCodes.Status409Conflict => Conflict,
        StatusCodes.Status413PayloadTooLarge => PayloadTooLarge,
        StatusCodes.Status429TooManyRequests => TooManyRequests,
        StatusCodes.Status503ServiceUnavailable => ServiceUnavailable,
        >= 500 => InternalError,
        _ => BadRequest,
    };
}
