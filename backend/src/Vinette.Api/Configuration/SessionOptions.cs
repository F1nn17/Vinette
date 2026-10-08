using System.ComponentModel.DataAnnotations;

namespace Vinette.Api.Configuration;

public sealed class SessionOptions : IValidatableObject
{
    public const string SectionName = "Session";

    /// <summary>Session expires after this period without activity.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromHours(2);

    /// <summary>Absolute session lifetime regardless of activity.</summary>
    public TimeSpan MaxLifetime { get; set; } = TimeSpan.FromDays(7);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (IdleTimeout <= TimeSpan.Zero)
        {
            yield return new ValidationResult("IdleTimeout must be positive.", [nameof(IdleTimeout)]);
        }

        if (MaxLifetime < IdleTimeout)
        {
            yield return new ValidationResult("MaxLifetime must not be shorter than IdleTimeout.", [nameof(MaxLifetime)]);
        }
    }
}
