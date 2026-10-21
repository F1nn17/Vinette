using MailKit.Net.Imap;
using Vinette.Core.Mail;

namespace Vinette.Mail;

/// <summary>
/// One authenticated IMAP connection. Not thread-safe by design: the connection manager lets only one command run at a time.
/// </summary>
internal sealed class ImapMailSession(ImapClient client) : IMailSession
{
    public bool IsConnected => client.IsConnected && client.IsAuthenticated;

    public Task PingAsync(CancellationToken cancellationToken) => client.NoOpAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (client.IsConnected)
            {
                await client.DisconnectAsync(quit: true, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or MailKit.ProtocolException or MailKit.ServiceNotConnectedException)
        {
            // The connection is being thrown away anyway.
        }
        finally
        {
            client.Dispose();
        }
    }
}
