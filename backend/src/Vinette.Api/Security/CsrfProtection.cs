using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;
using Vinette.Api.Configuration;
using Vinette.Api.Errors;

namespace Vinette.Api.Security;

/// <summary>The token a client must send in the X-CSRF-Token header with every state-changing request.</summary>
public sealed record CsrfResponse(string Token);

public static class CsrfProtection
{
    public const string HeaderName = "X-CSRF-Token";
    public const string CookieName = "__Host-vinette_csrf";

    /// <summary>Antiforgery (Data Protection based): a cookie plus a request token that the SPA keeps in memory.</summary>
    public static IServiceCollection AddVinetteCsrf(this IServiceCollection services)
    {
        services.AddAntiforgery(options =>
        {
            options.HeaderName = HeaderName;
            options.Cookie.Name = CookieName;
        });
        return services;
    }

    /// <summary>Add after UseAuthentication: the token is bound to the signed-in user.</summary>
    public static IApplicationBuilder UseVinetteCsrf(this IApplicationBuilder app) =>
        app.UseMiddleware<CsrfProtectionMiddleware>();

    /// <summary>GET /api/v1/csrf: issues a token (and its cookie). Safe and anonymous.</summary>
    public static IEndpointRouteBuilder MapCsrf(this IEndpointRouteBuilder api)
    {
        api.MapGet("/csrf", (HttpContext context, IAntiforgery antiforgery) =>
            {
                // GetTokens instead of GetAndStoreTokens: the latter refuses "Secure" cookies on requests that look like
                // plain HTTP (local dev, a TLS-terminating proxy), so the cookie is written here with fixed attributes.
                var tokens = antiforgery.GetTokens(context);
                if (tokens.CookieToken is not null)
                {
                    context.Response.Cookies.Append(CookieName, tokens.CookieToken, new CookieOptions
                    {
                        HttpOnly = true,
                        Secure = true,
                        SameSite = SameSiteMode.Strict,
                        Path = "/",
                        IsEssential = true,
                    });
                }

                context.Response.Headers.CacheControl = "no-store";
                return new CsrfResponse(tokens.RequestToken!);
            })
            .AllowAnonymous()
            .WithName("GetCsrfToken")
            .WithTags("Auth");
        return api;
    }
}

/// <summary>
/// Rejects state-changing requests that do not come from this application's pages (Origin/Referer) or lack a valid
/// anti-forgery token. The real-time hub is checked by Origin only: browsers cannot attach custom headers to a WebSocket.
/// </summary>
public sealed class CsrfProtectionMiddleware(
    RequestDelegate next,
    IAntiforgery antiforgery,
    IOptions<RequestSecurityOptions> options,
    IProblemDetailsService problemDetails)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var isHub = request.Path.StartsWithSegments("/hubs");
        var changesState = !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)
            || HttpMethods.IsOptions(request.Method) || HttpMethods.IsTrace(request.Method));

        if ((isHub || changesState)
            && !OriginPolicy.IsAllowed(request, options.Value.TrustedOrigins, requireHeader: changesState))
        {
            await RejectAsync(context, "The request does not come from this application.", ErrorCodes.OriginNotAllowed);
            return;
        }

        if (changesState && !isHub && !await antiforgery.IsRequestValidAsync(context))
        {
            await RejectAsync(context, "The security token is missing or no longer valid.", ErrorCodes.CsrfFailed);
            return;
        }

        await next(context);
    }

    private async Task RejectAsync(HttpContext context, string title, string code)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails =
            {
                Status = StatusCodes.Status403Forbidden,
                Title = title,
                Extensions = { ["code"] = code },
            },
        });
    }
}
