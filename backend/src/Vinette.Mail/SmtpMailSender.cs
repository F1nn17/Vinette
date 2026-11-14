using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Vinette.Core;
using Vinette.Core.Mail;

namespace Vinette.Mail;

/// <summary>How to reach the mail server's submission service. Plain data: Vinette.Api maps its validated options onto it.</summary>
/// <param name="Host">Server name; certificate validation checks it.</param>
/// <param name="Port">465 for implicit TLS, 587 for STARTTLS.</param>
/// <param name="Security">Tls = implicit TLS, StartTls = mandatory STARTTLS, None = plain (dev only).</param>
/// <param name="Timeout">Limit for connecting and for every server reply.</param>
/// <param name="AllowInvalidCertificate">Accept any server certificate; off by default, for a dev server with a self-signed certificate only.</param>
public sealed record SmtpConnectionSettings(
    string Host,
    int Port,
    ConnectionSecurity Security,
    TimeSpan Timeout,
    bool AllowInvalidCertificate = false);

/// <summary>
/// <see cref="IMailSender"/> over SMTP submission (RFC 6409) with MailKit: one short connection per message, signed in with the
/// user's own address and password, closed afterwards. The connection is never reused and a failed send is never repeated.
/// </summary>
public sealed class SmtpMailSender(SmtpConnectionSettings settings) : IMailSender
{
    public async Task SendAsync(MailCredentials credentials, OutgoingEnvelope envelope, ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Recipients.Count == 0)
        {
            throw new ArgumentException("A message needs at least one recipient.", nameof(envelope));
        }

        // The message goes out as it is: parsed only to be handed to the library, never changed.
        MimeMessage parsed;
        using (var stream = new MemoryStream(message.ToArray(), writable: false))
        {
            parsed = await MimeMessage.LoadAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        var sender = MailboxAddress.Parse(envelope.From);
        var recipients = envelope.Recipients.Select(MailboxAddress.Parse).ToList();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.Timeout);
        using var client = new SmtpClient
        {
            Timeout = (int)settings.Timeout.TotalMilliseconds,
            // The server wants a fully qualified name to be told; the domain of the mailbox is one that belongs to the installation.
            LocalDomain = sender.Domain,
        };
        if (settings.AllowInvalidCertificate)
        {
#pragma warning disable CA5359 // Explicit opt-in (SmtpConnectionSettings.AllowInvalidCertificate, dev servers only).
            client.ServerCertificateValidationCallback = ImapAuthenticator.AcceptAnyCertificate;
#pragma warning restore CA5359
        }

        var handedOver = false; // from the moment the message itself goes to the server, a broken connection leaves it unknown
        try
        {
            await client.ConnectAsync(settings.Host, settings.Port, ToSocketOptions(settings.Security), timeout.Token).ConfigureAwait(false);
            if (client.Capabilities.Contains(SmtpCapability.Authentication))
            {
                await client.AuthenticateAsync(credentials.Address, credentials.Password, timeout.Token).ConfigureAwait(false);
            }

            handedOver = true;
            _ = await client.SendAsync(new SendRequest(parsed, sender, recipients), timeout.Token).ConfigureAwait(false);
            await client.DisconnectAsync(quit: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (SmtpCommandException exception) when (IsRefusal(exception))
        {
            throw Refusal(exception);
        }
        catch (MailKit.Security.AuthenticationException exception)
        {
            throw new MailAccessRevokedException("The mail server rejected the credentials.", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MailUnavailableException($"Timed out after {settings.Timeout.TotalSeconds:0.#} s", exception, handedOver);
        }
        catch (Exception exception) when (exception is IOException or SocketException or ProtocolException or ServiceNotConnectedException
            or ServiceNotAuthenticatedException or CommandException or TimeoutException or SslHandshakeException)
        {
            // Type and message only: neither contains the credentials.
            throw new MailUnavailableException($"{exception.GetType().Name}: {exception.Message}", exception, handedOver);
        }
    }

    /// <summary>The server answered a command with a refusal (the connection is fine), as opposed to a broken connection.</summary>
    internal static bool IsRefusal(SmtpCommandException exception) =>
        exception.ErrorCode is SmtpErrorCode.RecipientNotAccepted or SmtpErrorCode.SenderNotAccepted or SmtpErrorCode.MessageNotAccepted
        || (int)exception.StatusCode >= 400;

    internal static MailRejectedException Refusal(SmtpCommandException exception)
    {
        // 552 means "too much" wherever it comes (at MAIL FROM, when the size is announced, or after the data).
        var reason = exception.StatusCode == SmtpStatusCode.ExceededStorageAllocation
            ? SendRejection.TooLarge
            : exception.ErrorCode switch
            {
                SmtpErrorCode.RecipientNotAccepted => SendRejection.Recipient,
                SmtpErrorCode.SenderNotAccepted => SendRejection.Sender,
                SmtpErrorCode.MessageNotAccepted => SendRejection.Content,
                _ => SendRejection.Other,
            };
        return new MailRejectedException(reason, $"The mail server refused the message ({(int)exception.StatusCode}).", exception)
        {
            Address = reason == SendRejection.Recipient ? exception.Mailbox?.Address : null,
        };
    }

    private static SecureSocketOptions ToSocketOptions(ConnectionSecurity security) => security switch
    {
        ConnectionSecurity.Tls => SecureSocketOptions.SslOnConnect,
        ConnectionSecurity.StartTls => SecureSocketOptions.StartTls,
        _ => SecureSocketOptions.None,
    };
}
