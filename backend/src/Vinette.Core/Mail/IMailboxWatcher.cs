namespace Vinette.Core.Mail;

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
        Func<MailboxEvent, ValueTask> onChange);
}
