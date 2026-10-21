using System.Net;
using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Vinette.Core;
using Vinette.Core.Authentication;

namespace Vinette.Mail;

/// <summary>
/// Checks credentials with IMAP LOGIN. A short-lived connection: connect, send the client identity (ID),
/// authenticate, disconnect. TLS certificates are validated unless the settings explicitly say otherwise.
/// </summary>
public sealed class ImapAuthenticator(ImapConnectionSettings settings) : IAuthenticator
{
    private const string ClientName = "Vinette";

    public async Task<AuthenticationResult> AuthenticateAsync(
        LoginRequest request,
        ClientInfo client,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(client);

        if (!IsAcceptableInput(request))
        {
            return AuthenticationResult.InvalidCredentials;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.Timeout);

        using var imap = new ImapClient { Timeout = (int)settings.Timeout.TotalMilliseconds };
        if (settings.AllowInvalidCertificate)
        {
#pragma warning disable CA5359 // Explicit opt-in (ImapConnectionSettings.AllowInvalidCertificate, dev servers only).
            imap.ServerCertificateValidationCallback = AcceptAnyCertificate;
#pragma warning restore CA5359
        }

        try
        {
            await imap.ConnectAsync(settings.Host, settings.Port, ToSocketOptions(settings.Security), timeout.Token);
            await IdentifyAsync(imap, client, timeout.Token);
            await imap.AuthenticateAsync(request.Address, request.Password, timeout.Token);
            await imap.DisconnectAsync(quit: true, CancellationToken.None);
            return AuthenticationResult.Success;
        }
        catch (AuthenticationException)
        {
            return AuthenticationResult.InvalidCredentials;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AuthenticationResult.Unavailable($"Timed out after {settings.Timeout.TotalSeconds:0.#} s");
        }
        catch (Exception exception) when (exception is IOException or SocketException or ProtocolException
            or ServiceNotConnectedException or ServiceNotAuthenticatedException or CommandException or TimeoutException
            or SslHandshakeException)
        {
            // Type and message only: neither contains the credentials.
            return AuthenticationResult.Unavailable($"{exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>
    /// The client identity for the ID command. "x-originating-ip" lets Dovecot log and rate-limit the real client
    /// instead of Vinette's address (honored only when Vinette is in Dovecot's login_trusted_networks).
    /// </summary>
    internal static ImapImplementation BuildIdentity(ClientInfo client)
    {
        var identity = new ImapImplementation
        {
            Name = ClientName,
            Version = typeof(ImapAuthenticator).Assembly.GetName().Version?.ToString(3),
        };

        // Only a well-formed IP address is forwarded: the value comes from request metadata.
        if (client.IpAddress is not null && IPAddress.TryParse(client.IpAddress, out var address))
        {
            identity.Properties["x-originating-ip"] = address.ToString();
        }

        return identity;
    }

    internal static async Task IdentifyAsync(ImapClient imap, ClientInfo client, CancellationToken cancellationToken)
    {
        if (!imap.Capabilities.HasFlag(ImapCapabilities.Id))
        {
            return;
        }

        try
        {
            await imap.IdentifyAsync(BuildIdentity(client), cancellationToken);
        }
        catch (CommandException)
        {
            // ID is informational; a server that refuses it must not block sign-in.
        }
    }

    private static bool IsAcceptableInput(LoginRequest request) =>
        !string.IsNullOrWhiteSpace(request.Address)
        && !string.IsNullOrEmpty(request.Password)
        && !HasControlCharacters(request.Address)
        && !HasControlCharacters(request.Password);

    private static bool HasControlCharacters(string value) => value.Any(char.IsControl);

    internal static SecureSocketOptions ToSocketOptions(ConnectionSecurity security) => security switch
    {
        ConnectionSecurity.Tls => SecureSocketOptions.SslOnConnect,
        ConnectionSecurity.StartTls => SecureSocketOptions.StartTls,
        ConnectionSecurity.None => SecureSocketOptions.None,
        _ => throw new ArgumentOutOfRangeException(nameof(security), security, null),
    };

    internal static bool AcceptAnyCertificate(
        object sender,
        System.Security.Cryptography.X509Certificates.X509Certificate? certificate,
        System.Security.Cryptography.X509Certificates.X509Chain? chain,
        System.Net.Security.SslPolicyErrors errors) => true;
}
