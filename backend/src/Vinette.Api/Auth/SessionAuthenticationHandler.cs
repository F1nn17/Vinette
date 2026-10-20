using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Vinette.Api.Errors;
using Vinette.Api.Sessions;
using Vinette.Core.Sessions;

namespace Vinette.Api.Auth;

public static class SessionAuth
{
    public const string Scheme = "VinetteSession";

    private const string SessionItemKey = "Vinette.Session";

    /// <summary>The session of the authenticated request (for opening the user's mail connection); null when anonymous.</summary>
    public static Session? GetSession(this HttpContext context) =>
        context.Items.TryGetValue(SessionItemKey, out var value) ? value as Session : null;

    internal static void SetSession(HttpContext context, Session session) => context.Items[SessionItemKey] = session;

    /// <summary>Cookie-session authentication; every endpoint requires it unless marked AllowAnonymous.</summary>
    public static IServiceCollection AddVinetteAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(Scheme)
            .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(Scheme, configureOptions: null);

        services.AddAuthorization(options =>
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        return services;
    }
}

/// <summary>
/// Authenticates a request by its session cookie. The principal carries only the mailbox address; the password
/// stays in the session store (see <see cref="SessionAuth.GetSession"/>). Failures answer with problem+json, never a redirect.
/// </summary>
public sealed class SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SessionService sessions,
    IProblemDetailsService problemDetails)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var session = await sessions.GetValidAsync(SessionCookie.Read(Request), Context.RequestAborted);
        if (session is null)
        {
            return AuthenticateResult.NoResult();
        }

        SessionAuth.SetSession(Context, session);
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, session.Address)],
            SessionAuth.Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SessionAuth.Scheme));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // A cookie that no longer maps to a live session is useless: tell the browser to drop it.
        if (SessionCookie.Read(Request) is not null)
        {
            SessionCookie.Clear(Response);
        }

        await WriteProblemAsync(StatusCodes.Status401Unauthorized, "Sign in required.", ErrorCodes.Unauthorized);
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        WriteProblemAsync(StatusCodes.Status403Forbidden, "Access denied.", ErrorCodes.Forbidden);

    private async Task WriteProblemAsync(int status, string title, string code)
    {
        Response.StatusCode = status;
        await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = Context,
            ProblemDetails =
            {
                Status = status,
                Title = title,
                Extensions = { ["code"] = code },
            },
        });
    }
}
