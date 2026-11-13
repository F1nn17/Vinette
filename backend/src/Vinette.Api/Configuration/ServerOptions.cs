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
    /// A mail connection that nobody has used for this long is closed to free the mail server; the next request of the
    /// user opens a new one. Shorter than <c>Session:IdleTimeout</c> makes sense: signed-in users with no activity should not hold connections.
    /// </summary>
    public TimeSpan ConnectionIdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Open mail connections of one mailbox (one per signed-in browser or device). Further requests wait, then fail with "busy".</summary>
    [Range(1, 1000)]
    public int MaxConnectionsPerUser { get; set; } = 5;

    /// <summary>Open mail connections of the whole application; keep it below what the mail server allows from one address.</summary>
    [Range(1, 100000)]
    public int MaxConnections { get; set; } = 200;

    /// <summary>
    /// The biggest mail, in bytes, that is loaded to be shown or to have an attachment downloaded: the application takes a mail
    /// apart in memory, so this bounds what one request can use. A bigger mail is refused without being downloaded (the
    /// user sees "too large to be shown"). Raise it only if your mail server accepts bigger mails and the application has
    /// memory for them.
    /// </summary>
    [Range(1_000_000L, 512_000_000L)]
    public long MaxViewableMessageBytes { get; set; } = Vinette.Core.Mail.MessageLimits.MaxViewableBytes;

    /// <summary>How long a request waits for a free connection place before it is answered with 503 and Retry-After.</summary>
    public TimeSpan ConnectionWaitTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Accept any TLS certificate of the mail server. Dev only (self-signed certificate); keep off in production,
    /// otherwise passwords can be intercepted.
    /// </summary>
    public bool AllowInvalidCertificate { get; set; }

    /// <summary>
    /// How often the connection that follows the inbox (IDLE) ends its wait and starts a new one: mail servers drop an IDLE that
    /// lasts too long (RFC 2177: 29 minutes). Keep it below that.
    /// </summary>
    public TimeSpan IdleRestart { get; set; } = TimeSpan.FromMinutes(25);

    /// <summary>How long the connection that follows a mailbox stays open after the last browser that listened to it has gone.</summary>
    public TimeSpan IdleGrace { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Connections that follow mailboxes, in the whole application (one per mailbox, whatever the number of browsers); keep it with <see cref="MaxConnections"/> below what the mail server allows.</summary>
    [Range(1, 100000)]
    public int MaxIdleConnections { get; set; } = 200;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (IdleRestart <= TimeSpan.Zero || IdleRestart > TimeSpan.FromMinutes(29))
        {
            yield return new ValidationResult("IdleRestart must be positive and at most 29 minutes.", [nameof(IdleRestart)]);
        }

        if (IdleGrace < TimeSpan.Zero)
        {
            yield return new ValidationResult("IdleGrace must not be negative.", [nameof(IdleGrace)]);
        }

        if (Timeout <= TimeSpan.Zero)
        {
            yield return new ValidationResult("Timeout must be positive.", [nameof(Timeout)]);
        }

        if (ConnectionWaitTimeout <= TimeSpan.Zero)
        {
            yield return new ValidationResult("ConnectionWaitTimeout must be positive.", [nameof(ConnectionWaitTimeout)]);
        }

        if (ConnectionIdleTimeout <= TimeSpan.Zero)
        {
            yield return new ValidationResult("ConnectionIdleTimeout must be positive.", [nameof(ConnectionIdleTimeout)]);
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
