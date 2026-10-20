using System.ComponentModel.DataAnnotations;

namespace Vinette.Api.Auth;

/// <summary>Body of <c>POST /api/v1/auth/login</c>.</summary>
public sealed record LoginRequestBody
{
    /// <summary>Mailbox address, used as the login.</summary>
    [Required, StringLength(254)]
    public string Address { get; init; } = string.Empty;

    [Required, StringLength(1024)]
    public string Password { get; init; } = string.Empty;

    /// <summary>"Stay signed in": the session lives for <c>Session:RememberMeLifetime</c> instead of until the browser closes.</summary>
    public bool RememberMe { get; init; }
}

/// <summary>The signed-in mailbox.</summary>
public sealed record LoginResponse(string Address);
