using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Vinette.Core.Authentication;
using Vinette.Core.Mail;

namespace Vinette.Mail;

/// <summary>Opens long-lived IMAP connections for <see cref="IMailGateway"/>: connect, identify, authenticate and keep the connection open.</summary>
public sealed class ImapMailSessionFactory(ImapConnectionSettings settings) : IMailSessionFactory
{
    public async Task<IMailSession> OpenAsync(MailCredentials credentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.Timeout);

        var client = new ImapClient { Timeout = (int)settings.Timeout.TotalMilliseconds };
        if (settings.AllowInvalidCertificate)
        {
#pragma warning disable CA5359 // Explicit opt-in (ImapConnectionSettings.AllowInvalidCertificate, dev servers only).
            client.ServerCertificateValidationCallback = ImapAuthenticator.AcceptAnyCertificate;
#pragma warning restore CA5359
        }

        try
        {
            await client.ConnectAsync(settings.Host, settings.Port, ImapAuthenticator.ToSocketOptions(settings.Security), timeout.Token).ConfigureAwait(false);
            await ImapAuthenticator.IdentifyAsync(client, new ClientInfo(credentials.ClientIp, null), timeout.Token).ConfigureAwait(false);
            await client.AuthenticateAsync(credentials.Address, credentials.Password, timeout.Token).ConfigureAwait(false);
            return new ImapMailSession(client);
        }
        catch (AuthenticationException exception)
        {
            client.Dispose();
            throw new MailAccessRevokedException("The mail server rejected the credentials.", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw new MailUnavailableException($"Timed out after {settings.Timeout.TotalSeconds:0.#} s", exception);
        }
        catch (Exception exception) when (exception is IOException or SocketException or ProtocolException
            or ServiceNotConnectedException or ServiceNotAuthenticatedException or CommandException or TimeoutException
            or SslHandshakeException)
        {
            client.Dispose();

            // Type and message only: neither contains the credentials.
            throw new MailUnavailableException($"{exception.GetType().Name}: {exception.Message}", exception);
        }
        catch
        {
            client.Dispose();
            throw; // unexpected, or the caller cancelled
        }
    }
}
