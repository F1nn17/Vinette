using Microsoft.Extensions.Options;
using Vinette.Api.Configuration;
using Vinette.Api.Errors;
using Vinette.Api.Sessions;
using Vinette.Core.Authentication;

namespace Vinette.Api.Auth;

public static partial class AuthEndpoints
{
    private const int MaxUserAgentLength = 256;

    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder api)
    {
        var auth = api.MapGroup("/auth").WithTags("Auth");

        auth.MapPost("/login", LoginAsync)
            .WithName("Login")
            .Produces<LoginResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        auth.MapPost("/logout", LogoutAsync)
            .WithName("Logout")
            .Produces(StatusCodes.Status204NoContent);

        return api;
    }

    private static async Task<IResult> LoginAsync(
        LoginRequestBody body,
        HttpContext http,
        IAuthenticator authenticator,
        SessionService sessions,
        IOptions<SessionLifetimeOptions> sessionOptions,
        TimeProvider time,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Vinette.Api.Auth");
        var address = body.Address.Trim();
        var clientIp = http.Connection.RemoteIpAddress?.ToString();
        var userAgent = http.Request.Headers.UserAgent.ToString();
        var client = new ClientInfo(clientIp, userAgent.Length > MaxUserAgentLength ? userAgent[..MaxUserAgentLength] : userAgent);

        var result = await authenticator.AuthenticateAsync(new LoginRequest(address, body.Password), client, cancellationToken);

        switch (result.Outcome)
        {
            case AuthenticationOutcome.InvalidCredentials:
                LogLoginRejected(logger, address, clientIp);
                return Results.Problem(
                    statusCode: StatusCodes.Status401Unauthorized,
                    title: "The address or password is incorrect.",
                    extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.InvalidCredentials });

            case AuthenticationOutcome.ServerUnavailable:
                LogMailServerUnavailable(logger, address, result.Diagnostic);
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "The mail server is temporarily unavailable.",
                    extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.MailUnavailable });
        }

        // A new id on every login (no session fixation); a session the browser still holds is ended.
        var previous = SessionCookie.Read(http.Request);
        if (previous is not null)
        {
            await sessions.EndAsync(previous, cancellationToken);
        }

        var session = await sessions.CreateAsync(address, body.Password, body.RememberMe, clientIp, cancellationToken);
        SessionCookie.Write(http.Response, session.Id, body.RememberMe, sessionOptions.Value, time);
        LogLoginSucceeded(logger, address, clientIp, body.RememberMe);

        return Results.Ok(new LoginResponse(session.Address));
    }

    private static async Task<IResult> LogoutAsync(HttpContext http, SessionService sessions, CancellationToken cancellationToken)
    {
        var sessionId = SessionCookie.Read(http.Request);
        if (sessionId is not null)
        {
            await sessions.EndAsync(sessionId, cancellationToken);
        }

        SessionCookie.Clear(http.Response);
        return Results.NoContent();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Login rejected for {Address} from {ClientIp}")]
    private static partial void LogLoginRejected(ILogger logger, string address, string? clientIp);

    [LoggerMessage(Level = LogLevel.Information, Message = "Login succeeded for {Address} from {ClientIp} (remember me: {RememberMe})")]
    private static partial void LogLoginSucceeded(ILogger logger, string address, string? clientIp, bool rememberMe);

    [LoggerMessage(Level = LogLevel.Error, Message = "Mail server unavailable during login of {Address}: {Diagnostic}")]
    private static partial void LogMailServerUnavailable(ILogger logger, string address, string? diagnostic);
}
