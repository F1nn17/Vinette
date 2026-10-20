namespace Vinette.Core.Authentication;

/// <summary>Who is logging in: the real client address (after trusted-proxy handling) and the browser.</summary>
/// <param name="IpAddress">Passed to the mail server so it can log and rate-limit the real client.</param>
/// <param name="UserAgent">Browser identification, informational.</param>
public sealed record ClientInfo(string? IpAddress, string? UserAgent);
