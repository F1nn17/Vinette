using System.ComponentModel.DataAnnotations;

namespace Vinette.Api.Configuration;

/// <summary>Bound to the "Attachments" section: where the files wait that the user attaches to a message, and how much may wait.</summary>
public sealed class AttachmentsOptions : IValidatableObject
{
    public const string SectionName = "Attachments";

    /// <summary>
    /// Directory for the uploaded files that are not sent yet. In production a directory of the persistent volume (the Docker image
    /// uses /data/attachments); with several instances of the application one that all of them share (an upload and the sending of the
    /// message may be served by different instances). Empty uses a directory under the system's temporary folder.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// The biggest single file, in bytes (default 20 MB). Files grow by about a third when they are sent, and the mail server has its own
    /// limit on a message: keep the files together below its limit divided by 1.4.
    /// </summary>
    [Range(1L, 2_000_000_000L)]
    public long MaxFileBytes { get; set; } = 20L * 1024 * 1024;

    /// <summary>What all the files of one mailbox that wait to be sent may take together, in bytes (default 25 MB).</summary>
    [Range(1L, 20_000_000_000L)]
    public long MaxTotalBytes { get; set; } = 25L * 1024 * 1024;

    /// <summary>The most files of one mailbox that wait to be sent (default 20).</summary>
    [Range(1, 1000)]
    public int MaxFiles { get; set; } = 20;

    /// <summary>How long a file that is not sent waits before it is removed (default 24 hours).</summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromHours(24);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Path is not null && Path.Contains('\0', StringComparison.Ordinal))
        {
            yield return new ValidationResult("Path is not a valid directory name.", [nameof(Path)]);
        }

        if (MaxTotalBytes < MaxFileBytes)
        {
            yield return new ValidationResult("MaxTotalBytes must not be less than MaxFileBytes.", [nameof(MaxTotalBytes)]);
        }

        if (Lifetime < TimeSpan.FromMinutes(1))
        {
            yield return new ValidationResult("Lifetime must be at least a minute.", [nameof(Lifetime)]);
        }
    }
}
