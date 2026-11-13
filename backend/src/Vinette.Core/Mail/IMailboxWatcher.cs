namespace Vinette.Core.Mail;

/// <summary>What the mail server reported about the inbox while it was being watched (the raw IMAP notifications).</summary>
public enum MailboxChangeKind
{
    /// <summary>The number of messages changed (mail arrived).</summary>
    CountChanged,

    /// <summary>A message was removed from the folder.</summary>
    Expunged,

    /// <summary>The marks of a message (read, flag, labels) changed.</summary>
    FlagsChanged,
}

/// <summary>One notification from the mail server about a folder.</summary>
/// <param name="Folder">The folder's path (only the inbox is watched for now).</param>
/// <param name="Kind">What happened.</param>
public sealed record MailboxChange(string Folder, MailboxChangeKind Kind);

/// <summary>
/// Follows a mailbox for changes without being asked (IMAP IDLE) and reports them. One watching connection is kept per
/// mailbox, whatever the number of web sessions that listen to it: it opens with the first listener, closes a while after the
/// last one is gone, and is kept apart from the connections that answer requests (<see cref="IMailGateway"/>).
/// </summary>
public interface IMailboxWatcher
{
    /// <summary>
    /// Starts listening to the mailbox <paramref name="address"/>. <paramref name="credentials"/> is asked for whenever the watching
    /// connection has to be (re)opened, so that the password is never kept: it returns the credentials of the web session
    /// <paramref name="sessionId"/> or null when that session has ended (the listener is then dropped). <paramref name="onChange"/> is
    /// called for every notification, one after another; an exception in it is swallowed. Dispose the result to stop listening.
    /// </summary>
    IDisposable Subscribe(
        string address,
        string sessionId,
        Func<CancellationToken, ValueTask<MailCredentials?>> credentials,
        Func<MailboxChange, ValueTask> onChange);
}
