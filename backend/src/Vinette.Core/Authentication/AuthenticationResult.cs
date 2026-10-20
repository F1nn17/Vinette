namespace Vinette.Core.Authentication;

public enum AuthenticationOutcome
{
    /// <summary>The mail server accepted the credentials.</summary>
    Succeeded,

    /// <summary>The mail server rejected the address or password.</summary>
    InvalidCredentials,

    /// <summary>The mail server could not be reached or failed (timeout, TLS, unexpected response).</summary>
    ServerUnavailable,
}

/// <param name="Outcome">What happened; the only part that may influence what the user is told.</param>
/// <param name="Diagnostic">
/// Technical reason for the server log (exception type and message). Never contains credentials and must
/// never be shown to the user.
/// </param>
public sealed record AuthenticationResult(AuthenticationOutcome Outcome, string? Diagnostic = null)
{
    public bool IsSuccess => Outcome == AuthenticationOutcome.Succeeded;

    public static AuthenticationResult Success { get; } = new(AuthenticationOutcome.Succeeded);

    public static AuthenticationResult InvalidCredentials { get; } = new(AuthenticationOutcome.InvalidCredentials);

    public static AuthenticationResult ServerUnavailable { get; } = new(AuthenticationOutcome.ServerUnavailable);

    public static AuthenticationResult Unavailable(string diagnostic) =>
        new(AuthenticationOutcome.ServerUnavailable, diagnostic);
}
