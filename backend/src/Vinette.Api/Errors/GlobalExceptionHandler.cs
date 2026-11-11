using Microsoft.AspNetCore.Diagnostics;

namespace Vinette.Api.Errors;

/// <summary>Turns unhandled exceptions into ProblemDetails without leaking internals.</summary>
public sealed partial class GlobalExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<GlobalExceptionHandler> logger,
    IHostEnvironment environment) : IExceptionHandler
{
    /// <summary>Non-standard status used by proxies when the client closed the connection.</summary>
    private const int ClientClosedRequest = 499;

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            LogClientCancelled();
            httpContext.Response.StatusCode = ClientClosedRequest;
            return true;
        }

        if (exception is BadHttpRequestException bad)
        {
            // A malformed request (a parameter that does not parse, a broken body): the caller's mistake, not a failure.
            LogBadRequest();
            httpContext.Response.StatusCode = bad.StatusCode;
            return await problemDetails.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails =
                {
                    Status = bad.StatusCode,
                    Title = "The request is malformed.",
                    Extensions = { ["code"] = ErrorCodes.ForStatus(bad.StatusCode) },
                },
            });
        }

        LogUnhandled(exception);
        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred.",
                // The message is shown only in Development; stack traces are never returned.
                Detail = environment.IsDevelopment() ? exception.Message : null,
                Extensions = { ["code"] = ErrorCodes.InternalError },
            },
        });
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "A malformed request was answered with an error")]
    private partial void LogBadRequest();

    [LoggerMessage(Level = LogLevel.Information, Message = "Request was cancelled by the client")]
    private partial void LogClientCancelled();

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception")]
    private partial void LogUnhandled(Exception exception);
}
