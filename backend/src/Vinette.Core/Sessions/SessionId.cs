using System.Security.Cryptography;

namespace Vinette.Core.Sessions;

/// <summary>Opaque session identifier: 256 random bits, base64url (43 characters). Carries no information.</summary>
public static class SessionId
{
    public const int Length = 43;

    public static string Create() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>Cheap shape check before hitting the store; does not prove the session exists.</summary>
    public static bool IsWellFormed(string? value) =>
        value is { Length: Length } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
