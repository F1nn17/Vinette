using System.ComponentModel.DataAnnotations;
using Vinette.Core;

namespace Vinette.Api.Configuration;

public sealed class ImapOptions : IValidatableObject
{
    public const string SectionName = "Imap";

    [Required]
    public string Host { get; set; } = "localhost";

    [Range(1, 65535)]
    public int Port { get; set; } = 993;

    public ConnectionSecurity Security { get; set; } = ConnectionSecurity.Tls;

    /// <summary>
    /// Limit for connecting and for every reply of the mail server. Must exceed Dovecot's delay for failed logins
    /// (up to ~15 s when many failures come from one address), or a wrong password looks like an outage.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Accept any TLS certificate of the mail server. Dev only (self-signed certificate); keep off in production,
    /// otherwise passwords can be intercepted.
    /// </summary>
    public bool AllowInvalidCertificate { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Timeout <= TimeSpan.Zero)
        {
            yield return new ValidationResult("Timeout must be positive.", [nameof(Timeout)]);
        }
    }
}

public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    [Required]
    public string Host { get; set; } = "localhost";

    [Range(1, 65535)]
    public int Port { get; set; } = 587;

    public ConnectionSecurity Security { get; set; } = ConnectionSecurity.StartTls;
}
