using Microsoft.AspNetCore.Diagnostics;
using Vinette.Api.Auth;
using Vinette.Api.Sessions;
using Vinette.Core.Mail;

namespace Vinette.Api.Errors;

/// <summary>
/// Turns failures of the mail gateway into answers. A mail server that no longer accepts the stored password
/// (changed or revoked) ends the web session at once: it can never work again, and the stored password must not be tried
/// further. A server that cannot be reached is a temporary 503.
/// </summary>
public sealed partial class MailExceptionHandler(
    IServiceProvider services,
    IProblemDetailsService problemDetails,
    ILogger<MailExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        switch (exception)
        {
            case MailAccessRevokedException:
                await EndSessionAsync(httpContext, cancellationToken);
                SessionCookie.Clear(httpContext.Response);
                LogRevoked(httpContext.User.Identity?.Name);
                return await WriteAsync(
                    httpContext,
                    StatusCodes.Status401Unauthorized,
                    "The mail server no longer accepts the saved password. Sign in again.",
                    ErrorCodes.SessionRevoked);

            case MailUnavailableException:
                LogUnavailable(httpContext.User.Identity?.Name, exception);
                return await WriteAsync(
                    httpContext,
                    StatusCodes.Status503ServiceUnavailable,
                    "The mail server is temporarily unavailable.",
                    ErrorCodes.MailUnavailable);

            default:
                return false;
        }
    }

    private async Task EndSessionAsync(HttpContext httpContext, CancellationToken cancellationToken)
    {
        // Resolved only now: hosts without sessions (tests of the error pipeline) do not need them.
        var sessionId = SessionCookie.Read(httpContext.Request);
        if (sessionId is null)
        {
            return;
        }

        await services.GetRequiredService<SessionService>().EndAsync(sessionId, cancellationToken);
        await services.GetRequiredService<IMailGateway>().ReleaseAsync(sessionId, cancellationToken);
    }

    private async ValueTask<bool> WriteAsync(HttpContext httpContext, int status, string title, string code)
    {
        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = { Status = status, Title = title, Extensions = { ["code"] = code } },
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The mail server rejected the saved password of {Address}: session ended")]
    private partial void LogRevoked(string? address);

    // The exception text names its type and the mail server's message, never the credentials.
    [LoggerMessage(Level = LogLevel.Warning, Message = "The mail server is unavailable for {Address}")]
    private partial void LogUnavailable(string? address, Exception exception);
}
