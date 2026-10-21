using System.ComponentModel.DataAnnotations;

namespace Vinette.Api.Configuration;

/// <summary>Bound to the "Security:LoginRateLimit" section: protection of the sign-in form against password guessing.</summary>
public sealed class LoginRateLimitOptions : IValidatableObject
{
    public const string SectionName = "Security:LoginRateLimit";

    /// <summary>Wrong passwords for one mailbox before it is locked (the lock then grows 1, 2, 4 ... minutes).</summary>
    [Range(1, 1000)]
    public int MaxFailuresPerAccount { get; set; } = 5;

    /// <summary>Wrong passwords from one client address before it is locked, whatever mailboxes it tried.</summary>
    [Range(1, 10_000)]
    public int MaxFailuresPerIp { get; set; } = 20;

    /// <summary>Failures older than this (with no newer failure) are forgotten.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Upper limit of one lock.</summary>
    public TimeSpan MaxLockout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Sign-in requests per minute from one client address, successful or not (protects the mail server).</summary>
    [Range(1, 100_000)]
    public int MaxAttemptsPerMinutePerIp { get; set; } = 30;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Window <= TimeSpan.Zero)
        {
            yield return new ValidationResult("Window must be positive.", [nameof(Window)]);
        }

        if (MaxLockout < TimeSpan.FromMinutes(1))
        {
            yield return new ValidationResult("MaxLockout must be at least one minute.", [nameof(MaxLockout)]);
        }
    }
}
