using Vinette.Core;

namespace Vinette.Mail;

/// <summary>How to reach the IMAP server. Plain data: Vinette.Api maps its validated options onto it.</summary>
/// <param name="Host">Server name; certificate validation checks it.</param>
/// <param name="Port">993 for implicit TLS, 143 for STARTTLS.</param>
/// <param name="Security">Tls = implicit TLS, StartTls = mandatory STARTTLS, None = plain (dev only).</param>
/// <param name="Timeout">Limit for connecting and for every server reply.</param>
/// <param name="AllowInvalidCertificate">
/// Accept any server certificate. Off by default; for a dev server with a self-signed certificate only.
/// </param>
public sealed record ImapConnectionSettings(
    string Host,
    int Port,
    ConnectionSecurity Security,
    TimeSpan Timeout,
    bool AllowInvalidCertificate = false);
