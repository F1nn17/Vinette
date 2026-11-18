using Vinette.Api.Errors;
using Vinette.Core.Attachments;
using Vinette.Core.Mail;

namespace Vinette.Api.Mail;

/// <summary>Puts the files of a message among the mailbox's uploaded files (VIN-093), so that the editor can attach them to a message it is writing.</summary>
internal static class StoredFiles
{
    /// <summary>
    /// Stores every file, or none: if one is refused by the limits of the store, those already stored are removed and the refusal is
    /// returned as the answer (413 <c>attachment_too_large</c> or <c>attachments_too_large</c>, 409 <c>too_many_attachments</c>).
    /// </summary>
    public static async Task<(List<StoredAttachment> Stored, IResult? Problem)> SaveAllAsync(
        IAttachmentStore store,
        string owner,
        IEnumerable<OutgoingAttachment> files,
        CancellationToken cancellationToken)
    {
        var stored = new List<StoredAttachment>();
        try
        {
            foreach (var file in files)
            {
                using var content = new MemoryStream(file.Content.ToArray(), writable: false);
                stored.Add(await store.SaveAsync(owner, file.FileName, file.ContentType, content, cancellationToken));
            }

            return (stored, null);
        }
        catch (Exception exception)
        {
            // Not all of the files could be taken: none is left behind.
            foreach (var done in stored)
            {
                _ = await store.RemoveAsync(owner, done.Id, CancellationToken.None);
            }

            if (exception is AttachmentRefusedException refused)
            {
                return ([], refused.Reason switch
                {
                    AttachmentRefusal.FileTooLarge => Problem(StatusCodes.Status413PayloadTooLarge, "A file of the message is too large.", ErrorCodes.AttachmentTooLarge),
                    AttachmentRefusal.TotalTooLarge => Problem(StatusCodes.Status413PayloadTooLarge, "The files of the message are too large together.", ErrorCodes.AttachmentsTooLarge),
                    _ => Problem(StatusCodes.Status409Conflict, "Too many files are attached.", ErrorCodes.TooManyAttachments),
                });
            }

            throw;
        }
    }

    private static IResult Problem(int status, string title, string code) =>
        Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });
}
