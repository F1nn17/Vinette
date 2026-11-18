using Vinette.Core.Attachments;
using Vinette.Core.Mail;

namespace Vinette.Api.Mail;

/// <summary>Reads the uploaded files a message refers to (VIN-093) into what the composer attaches.</summary>
internal static class OutgoingFiles
{
    /// <summary>
    /// The files named by <paramref name="ids"/> (each once), or null if one of them is not there (any more, or not this mailbox's).
    /// </summary>
    public static async Task<List<OutgoingAttachment>?> LoadAsync(IAttachmentStore store, string owner, IEnumerable<string>? ids, CancellationToken cancellationToken)
    {
        var files = new List<OutgoingAttachment>();
        foreach (var id in (ids ?? []).Distinct(StringComparer.Ordinal))
        {
            await using var content = await store.OpenAsync(owner, id, cancellationToken);
            if (content is null)
            {
                return null;
            }

            using var buffer = new MemoryStream();
            await content.Content.CopyToAsync(buffer, cancellationToken);
            files.Add(new OutgoingAttachment(content.Attachment.FileName, content.Attachment.ContentType, buffer.ToArray()));
        }

        return files;
    }
}
