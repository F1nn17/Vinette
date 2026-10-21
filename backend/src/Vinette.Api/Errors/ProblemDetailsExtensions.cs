using Microsoft.AspNetCore.Mvc;

namespace Vinette.Api.Errors;

public static class ProblemDetailsExtensions
{
    /// <summary>Registers ProblemDetails (RFC 9457) with "code" and "correlationId" members, exception handling and validation.</summary>
    public static IServiceCollection AddVinetteProblemDetails(this IServiceCollection services)
    {
        services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
            {
                var problem = context.ProblemDetails;
                var status = problem.Status ?? context.HttpContext.Response.StatusCode;

                if (!problem.Extensions.TryGetValue("code", out var code))
                {
                    code = problem is HttpValidationProblemDetails
                        ? ErrorCodes.ValidationFailed
                        : ErrorCodes.ForStatus(status);
                    problem.Extensions["code"] = code;
                }

                problem.Type = ErrorCodes.TypePrefix + code;

                if (CorrelationIdMiddleware.Get(context.HttpContext) is { } correlationId)
                {
                    problem.Extensions["correlationId"] = correlationId;
                }
            };
        });
        services.AddExceptionHandler<MailExceptionHandler>();
        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.AddValidation();
        return services;
    }

    /// <summary>Correlation id, exception handler and ProblemDetails bodies for empty 4xx/5xx responses. Add first in the pipeline.</summary>
    public static IApplicationBuilder UseVinetteProblemDetails(this IApplicationBuilder app)
    {
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        return app;
    }
}
