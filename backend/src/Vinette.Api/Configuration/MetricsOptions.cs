using System.ComponentModel.DataAnnotations;

namespace Vinette.Api.Configuration;

/// <summary>Prometheus metrics endpoint (<c>GET /metrics</c>). Off by default; when on, it needs a bearer token.</summary>
public sealed class MetricsOptions : IValidatableObject
{
    public const string SectionName = "Metrics";

    /// <summary>Longest token that is accepted as too short to be safe.</summary>
    public const int MinTokenLength = 16;

    public bool Enabled { get; set; }

    /// <summary>
    /// The scraper sends it as <c>Authorization: Bearer &lt;token&gt;</c>. Keep it in an environment variable or a protected
    /// config file; it is never logged. Generate a long random value, e.g. <c>openssl rand -hex 32</c>.
    /// </summary>
    public string? BearerToken { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Enabled && (BearerToken is null || BearerToken.Length < MinTokenLength))
        {
            yield return new ValidationResult(
                $"Metrics:BearerToken of at least {MinTokenLength} characters is required when Metrics:Enabled is true.",
                [nameof(BearerToken)]);
        }
    }
}
