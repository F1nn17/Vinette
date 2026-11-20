using System.ComponentModel.DataAnnotations;

namespace Vinette.Api.Configuration;

/// <summary>Bound to the "Scheduling" section: the queue of messages that are sent later (VIN-107, ADR-0008).</summary>
public sealed class SchedulingOptions : IValidatableObject
{
    public const string SectionName = "Scheduling";

    /// <summary>
    /// Directory of the queue: two files for each message, the description with the encrypted mailbox password and the mail itself. In production
    /// a directory of the persistent volume (the Docker image uses /data/scheduled); with several instances one that all of them share. Empty
    /// uses a directory under the system's temporary folder (messages are lost with it).
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Whether this instance sends the messages whose time has come. With several instances behind one address turn it on for exactly one:
    /// two senders would send a message twice. Messages can be scheduled and cancelled through any instance either way.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the queue is looked at (default 30 seconds).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The longest a message may be put off (default 7 days). The mailbox password is kept this long, plus <see cref="RetryFor"/> and a day.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromDays(7);

    /// <summary>The soonest a message may be scheduled for, from now (default 1 minute): a time that has all but come is a mistake, and "Send" does that.</summary>
    public TimeSpan MinDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>The most scheduled messages one mailbox may have (default 50).</summary>
    [Range(1, 1000)]
    public int MaxPerMailbox { get; set; } = 50;

    /// <summary>How long, after its time, sending is tried again while the mail server cannot be reached (default 1 hour); then the message goes to the drafts folder.</summary>
    public TimeSpan RetryFor { get; set; } = TimeSpan.FromHours(1);

    /// <summary>The pause between tries (default 5 minutes).</summary>
    public TimeSpan RetryEvery { get; set; } = TimeSpan.FromMinutes(5);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Path is not null && Path.Contains('\0', StringComparison.Ordinal))
        {
            yield return new ValidationResult("Path is not a valid directory name.", [nameof(Path)]);
        }

        if (PollInterval < TimeSpan.FromSeconds(1) || PollInterval > TimeSpan.FromHours(1))
        {
            yield return new ValidationResult("PollInterval must be between 1 second and 1 hour.", [nameof(PollInterval)]);
        }

        if (MaxDelay < TimeSpan.FromMinutes(5) || MaxDelay > TimeSpan.FromDays(366))
        {
            yield return new ValidationResult("MaxDelay must be between 5 minutes and 366 days.", [nameof(MaxDelay)]);
        }

        if (MinDelay < TimeSpan.Zero || MinDelay >= MaxDelay)
        {
            yield return new ValidationResult("MinDelay must not be negative and must be less than MaxDelay.", [nameof(MinDelay)]);
        }

        if (RetryFor < TimeSpan.Zero || RetryFor > TimeSpan.FromDays(1))
        {
            yield return new ValidationResult("RetryFor must be between zero and 1 day.", [nameof(RetryFor)]);
        }

        if (RetryEvery < TimeSpan.FromSeconds(10) || RetryEvery > TimeSpan.FromHours(6))
        {
            yield return new ValidationResult("RetryEvery must be between 10 seconds and 6 hours.", [nameof(RetryEvery)]);
        }
    }
}
