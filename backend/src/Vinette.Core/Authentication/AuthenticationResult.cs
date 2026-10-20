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

public sealed record AuthenticationResult(AuthenticationOutcome Outcome)
{
    public bool IsSuccess => Outcome == AuthenticationOutcome.Succeeded;

    public static AuthenticationResult Success { get; } = new(AuthenticationOutcome.Succeeded);

    public static AuthenticationResult InvalidCredentials { get; } = new(AuthenticationOutcome.InvalidCredentials);

    public static AuthenticationResult ServerUnavailable { get; } = new(AuthenticationOutcome.ServerUnavailable);
}
