using Microsoft.Extensions.Options;
using Vinette.Api.Configuration;
using Vinette.Core;

namespace Vinette.Api.Security;

/// <summary>
/// Says in the log, once at start, when the connection to the mail server is not protected (VIN-122): the mailbox password goes over it on every
/// sign-in. Nothing is refused: a server on the same machine or a private network is a normal set-up, and the administrator may know better;
/// the warning is for the one who set <c>Security</c> to <c>None</c> by habit, or turned the certificate check off to make a test work and forgot.
/// </summary>
public sealed partial class TransportSecurityWarnings(
    IOptions<ImapOptions> imap,
    IOptions<SmtpOptions> smtp,
    ILogger<TransportSecurityWarnings> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Check("Imap", imap.Value.Host, imap.Value.Security, imap.Value.AllowInvalidCertificate);
        Check("Smtp", smtp.Value.Host, smtp.Value.Security, smtp.Value.AllowInvalidCertificate);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Check(string section, string host, ConnectionSecurity security, bool allowInvalidCertificate)
    {
        if (security == ConnectionSecurity.None && !IsThisMachine(host))
        {
            Unencrypted(logger, section, host);
        }

        if (allowInvalidCertificate)
        {
            CertificateNotChecked(logger, section);
        }
    }

    /// <summary>The mail server is on the machine itself, where nothing crosses a network.</summary>
    internal static bool IsThisMachine(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address));

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "{Section}:Security is None: the mailbox password and the mail travel unencrypted to {Host}. Use Tls or StartTls unless this is a private network you trust.")]
    private static partial void Unencrypted(ILogger logger, string section, string host);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "{Section}:AllowInvalidCertificate is on: the certificate of the mail server is not checked, so the password can be intercepted. Meant for tests only.")]
    private static partial void CertificateNotChecked(ILogger logger, string section);
}
