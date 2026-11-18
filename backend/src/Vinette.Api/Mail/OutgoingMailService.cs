using Vinette.Core.Mail;

namespace Vinette.Api.Mail;

/// <summary>
/// <see cref="IOutgoingMail"/>: compose, send, then keep a copy in the "Sent" folder. The copy is kept on the user's ordinary
/// connection (<see cref="IMailGateway"/>), once, never repeated; a folder the mailbox does not have is not created (the mailbox is
/// the user's, and its layout is theirs), and a copy that fails is reported, not retried: the message is out, which is what counts.
/// </summary>
public sealed partial class OutgoingMailService(
    IMessageComposer composer,
    IMailSender sender,
    IMailGateway gateway,
    ILogger<OutgoingMailService> logger,
    TimeProvider? timeProvider = null) : IOutgoingMail
{
    /// <summary>How long the copy may take; it is not tied to the request, which may be gone once the message is out.</summary>
    private static readonly TimeSpan CopyTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    public async Task<SendOutcome> SendAsync(MailCredentials credentials, OutgoingMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(message);

        var composed = composer.Compose(message);
        await sender.SendAsync(credentials, composed.Envelope, composed.Raw, cancellationToken).ConfigureAwait(false);

        // The message is out. From here on nothing may fail the call, nor may a client that has gone away cancel the copy.
        using var timeout = new CancellationTokenSource(CopyTimeout);
        try
        {
            var sentAt = time.GetUtcNow();
            var (outcome, id) = await gateway.ExecuteAsync(
                credentials,
                async (session, token) =>
                {
                    var folders = FolderRoleGuesser.Complete(await session.GetFoldersAsync(token).ConfigureAwait(false)); // the same roles as the sidebar shows
                    var sent = folders.FirstOrDefault(f => f.Role == FolderRole.Sent && f.CanContainMessages);
                    return sent is null
                        ? (SentCopyOutcome.NoSentFolder, (MessageId?)null)
                        : (SentCopyOutcome.Saved, await session.AppendMessageAsync(sent.Path, composed.Raw, seen: true, draft: false, sentAt, token).ConfigureAwait(false));
                },
                timeout.Token,
                safeToRepeat: false).ConfigureAwait(false);
            if (outcome == SentCopyOutcome.NoSentFolder)
            {
                NoSentFolder(logger);
            }

            return new SendOutcome(composed, outcome, id);
        }
        catch (Exception exception) when (exception is MailUnavailableException or MailBusyException or MailAccessRevokedException
            or MailFolderNotFoundException or OperationCanceledException)
        {
            CopyFailed(logger, exception.GetType().Name); // the type only: what the server said may name addresses
            return new SendOutcome(composed, SentCopyOutcome.Failed, null);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A message was sent, but the mailbox has no Sent folder to keep a copy in")]
    private static partial void NoSentFolder(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A message was sent, but its copy could not be saved in the Sent folder ({Reason})")]
    private static partial void CopyFailed(ILogger logger, string reason);
}
