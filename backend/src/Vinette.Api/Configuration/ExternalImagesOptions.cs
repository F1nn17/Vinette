using System.ComponentModel.DataAnnotations;

namespace Vinette.Api.Configuration;

/// <summary>
/// Bound to the "ExternalImages" section: pictures that a mail takes from the web. They are never loaded by the reader's
/// browser (that would tell the sender who read the mail, when, and from where); when the reader asks for them, the
/// application fetches them itself and puts them into the message.
/// </summary>
public sealed class ExternalImagesOptions : IValidatableObject
{
    public const string SectionName = "ExternalImages";

    /// <summary>Whether readers can ask to see external pictures. Off: they are always hidden.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The biggest picture that is fetched, in bytes; a bigger one is dropped.</summary>
    [Range(1_000, 20_000_000)]
    public int MaxImageBytes { get; set; } = 2_000_000;

    /// <summary>The most picture bytes embedded into one message.</summary>
    [Range(1_000, 100_000_000)]
    public int MaxTotalBytes { get; set; } = 10_000_000;

    /// <summary>The most pictures fetched for one message.</summary>
    [Range(1, 100)]
    public int MaxImages { get; set; } = 30;

    /// <summary>How long one picture may take, counting every redirect.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long all the pictures of one message may take together.</summary>
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How many redirects are followed (each one is checked like the first address).</summary>
    [Range(0, 10)]
    public int MaxRedirects { get; set; } = 3;

    /// <summary>The ports pictures are fetched from; anything else is refused.</summary>
    public int[] AllowedPorts { get; set; } = [80, 443];

    /// <summary>
    /// Fetch from private, loopback and link-local addresses too. <b>Never on a real server</b>: a mail could make the
    /// application read pages of your internal network. For tests and a development machine only.
    /// </summary>
    public bool AllowPrivateAddresses { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(1))
        {
            yield return new ValidationResult("RequestTimeout must be between 0 and one minute.", [nameof(RequestTimeout)]);
        }

        if (TotalTimeout < RequestTimeout || TotalTimeout > TimeSpan.FromMinutes(5))
        {
            yield return new ValidationResult("TotalTimeout must be at least RequestTimeout and at most five minutes.", [nameof(TotalTimeout)]);
        }

        if (MaxTotalBytes < MaxImageBytes)
        {
            yield return new ValidationResult("MaxTotalBytes must not be smaller than MaxImageBytes.", [nameof(MaxTotalBytes)]);
        }

        if (AllowedPorts is null || AllowedPorts.Length == 0 || AllowedPorts.Any(p => p is < 1 or > 65535))
        {
            yield return new ValidationResult("AllowedPorts must list ports from 1 to 65535.", [nameof(AllowedPorts)]);
        }
    }
}
