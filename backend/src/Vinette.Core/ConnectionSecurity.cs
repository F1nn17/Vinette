namespace Vinette.Core;

/// <summary>How a connection to the mail server is secured.</summary>
public enum ConnectionSecurity
{
    /// <summary>Implicit TLS (IMAP 993, SMTP 465).</summary>
    Tls,

    /// <summary>Plain connection upgraded with STARTTLS (SMTP 587).</summary>
    StartTls,

    /// <summary>No encryption. For local development against a test server only.</summary>
    None,
}
