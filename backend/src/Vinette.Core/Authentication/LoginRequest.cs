namespace Vinette.Core.Authentication;

/// <summary>
/// Credentials entered on the login screen. The password lives in memory only for the duration of the request;
/// it is never logged (see <see cref="ToString"/>) and is stored only in protected form (<see cref="Sessions.Session"/>).
/// </summary>
public sealed record LoginRequest(string Address, string Password)
{
    /// <summary>Redacted: the generated ToString of a record would print the password.</summary>
    public override string ToString() => $"LoginRequest {{ Address = {Address}, Password = *** }}";
}
