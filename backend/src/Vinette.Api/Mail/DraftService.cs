using Vinette.Core.Mail;

namespace Vinette.Api.Mail;

/// <summary>
/// <see cref="IDrafts"/>: a draft is a mail in the drafts folder (the same role the sidebar shows). A new version is appended first and the
/// old one removed after, so that a failure in between leaves two versions rather than none; appending is never repeated by the gateway.
/// </summary>
public sealed class DraftService(IMessageComposer composer, IMessageParser parser, IMailGateway gateway, TimeProvider? timeProvider = null) : IDrafts
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    public async Task<SavedDraft?> SaveAsync(MailCredentials credentials, OutgoingMessage message, MessageId? previous, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(message);

        var composed = composer.ComposeDraft(message);
        var savedAt = time.GetUtcNow();
        return await gateway.ExecuteAsync(
            credentials,
            async (session, token) =>
            {
                var drafts = await DraftsFolderAsync(session, token).ConfigureAwait(false);
                if (drafts is null)
                {
                    return null;
                }

                if (previous is { } old && !IsIn(old, drafts))
                {
                    throw new MailMessageNotFoundException("The message is not in the drafts folder.");
                }

                var id = await session.AppendMessageAsync(drafts.Path, composed.Raw, seen: true, draft: true, savedAt, token).ConfigureAwait(false);
                var removed = true;
                if (previous is { } replaced)
                {
                    try
                    {
                        await session.DeleteMessagePermanentlyAsync(replaced, token).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is MailMessageNotFoundException or UidValidityChangedException)
                    {
                        // Already gone: what was wanted.
                    }
                    catch (Exception exception) when (exception is MailUnavailableException or MailBusyException or OperationCanceledException)
                    {
                        removed = false; // the new version is kept; the answer must say so
                    }
                }

                return new SavedDraft(id, composed.MessageId, savedAt, removed);
            },
            cancellationToken,
            safeToRepeat: false).ConfigureAwait(false);
    }

    public async Task<OpenedDraft?> OpenAsync(MailCredentials credentials, MessageId id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var source = await gateway.ExecuteAsync(
            credentials,
            async (session, token) =>
            {
                var drafts = await DraftsFolderAsync(session, token).ConfigureAwait(false);
                if (drafts is null)
                {
                    return null;
                }

                return IsIn(id, drafts)
                    ? await session.GetMessageAsync(id, token).ConfigureAwait(false)
                    : throw new MailMessageNotFoundException("The message is not in the drafts folder.");
            },
            cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return null;
        }

        MemoryStream Open() => new(source.Content, writable: false);
        ParsedMessage parsed;
        await using (var stream = Open())
        {
            parsed = await parser.ParseAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        var files = new List<OutgoingAttachment>(parsed.Attachments.Count);
        foreach (var attachment in parsed.Attachments.Where(a => !a.IsMessage))
        {
            await using var stream = Open();
            if (await parser.ExtractAttachmentAsync(stream, attachment.PartId, cancellationToken).ConfigureAwait(false) is { } content)
            {
                files.Add(new OutgoingAttachment(attachment.FileName, attachment.ContentType, content.Content));
            }
        }

        var message = new OutgoingMessage(
            parsed.From ?? new MailAddressInfo(string.Empty, credentials.Address),
            parsed.To,
            parsed.Cc,
            parsed.Bcc ?? [],
            parsed.Subject,
            parsed.Text,
            parsed.Html,
            files,
            parsed.InReplyTo,
            parsed.References,
            parsed.MessageId,
            parsed.Date,
            parsed.ReceiptTo is not null);
        return new OpenedDraft(id, message);
    }

    public async Task<bool> DeleteAsync(MailCredentials credentials, MessageId id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        return await gateway.ExecuteAsync(
            credentials,
            async (session, token) =>
            {
                var drafts = await DraftsFolderAsync(session, token).ConfigureAwait(false);
                if (drafts is null)
                {
                    return false;
                }

                if (!IsIn(id, drafts))
                {
                    throw new MailMessageNotFoundException("The message is not in the drafts folder.");
                }

                await session.DeleteMessagePermanentlyAsync(id, token).ConfigureAwait(false);
                return true;
            },
            cancellationToken,
            safeToRepeat: false).ConfigureAwait(false);
    }

    private static async Task<MailFolderInfo?> DraftsFolderAsync(IMailSession session, CancellationToken cancellationToken)
    {
        var folders = FolderRoleGuesser.Complete(await session.GetFoldersAsync(cancellationToken).ConfigureAwait(false)); // the same roles as the sidebar shows
        return folders.FirstOrDefault(f => f.Role == FolderRole.Drafts && f.CanContainMessages);
    }

    private static bool IsIn(MessageId id, MailFolderInfo drafts) => string.Equals(id.Folder, drafts.Path, StringComparison.Ordinal);
}
