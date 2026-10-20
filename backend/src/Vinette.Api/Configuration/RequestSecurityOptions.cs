using System.ComponentModel.DataAnnotations;

namespace Vinette.Api.Configuration;

/// <summary>Bound to the "Security" section.</summary>
public sealed class RequestSecurityOptions : IValidatableObject
{
    public const string SectionName = "Security";

    /// <summary>
    /// Origins (scheme://host[:port], no path) whose pages may send state-changing requests, in addition to the
    /// application's own address. Needed when the browser address differs from the server's view of it
    /// (a front-end dev server, unusual proxy setups).
    /// </summary>
    public string[] TrustedOrigins { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var origin in TrustedOrigins)
        {
            if (!IsOrigin(origin))
            {
                yield return new ValidationResult(
                    $"'{origin}' is not an origin: use scheme://host[:port] without a path.",
                    [nameof(TrustedOrigins)]);
            }
        }
    }

    internal static bool IsOrigin(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && string.Equals(value, uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
}
