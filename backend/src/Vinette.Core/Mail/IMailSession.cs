namespace Vinette.Core.Mail;

/// <summary>
/// One authenticated connection to the user's mailbox. A session is never used by two callers at the same time:
/// <see cref="IMailGateway"/> serializes the commands. Mail operations (folders, messages) are added here by the
/// features that need them; callers only ever see this interface, never the protocol library behind it.
/// </summary>
public interface IMailSession : IAsyncDisposable
{
    /// <summary>True while the connection to the mail server is open and authenticated.</summary>
    bool IsConnected { get; }

    /// <summary>Keeps the connection alive and detects a dead one (the IMAP NOOP command).</summary>
    Task PingAsync(CancellationToken cancellationToken);
}
