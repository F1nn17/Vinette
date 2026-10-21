using System.ComponentModel.DataAnnotations;

namespace Vinette.Api.Configuration;

public sealed class SessionLifetimeOptions : IValidatableObject
{
    public const string SectionName = "Session";

    /// <summary>Where sessions are kept: <c>InMemory</c> (default) or <c>Redis</c> (several instances; needs the Redis section).</summary>
    public SessionStoreKind Store { get; set; } = SessionStoreKind.InMemory;

    /// <summary>Session expires after this period without activity.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromHours(2);

    /// <summary>Absolute session lifetime regardless of activity.</summary>
    public TimeSpan MaxLifetime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Lifetime of a session when the user chose "stay signed in"; counted both as idle and as absolute limit.</summary>
    public TimeSpan RememberMeLifetime { get; set; } = TimeSpan.FromDays(30);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enum.IsDefined(Store))
        {
            yield return new ValidationResult("Store must be InMemory or Redis.", [nameof(Store)]);
        }

        if (IdleTimeout <= TimeSpan.Zero)
        {
            yield return new ValidationResult("IdleTimeout must be positive.", [nameof(IdleTimeout)]);
        }

        if (MaxLifetime < IdleTimeout)
        {
            yield return new ValidationResult("MaxLifetime must not be shorter than IdleTimeout.", [nameof(MaxLifetime)]);
        }

        if (RememberMeLifetime < MaxLifetime)
        {
            yield return new ValidationResult("RememberMeLifetime must not be shorter than MaxLifetime.", [nameof(RememberMeLifetime)]);
        }
    }
}
