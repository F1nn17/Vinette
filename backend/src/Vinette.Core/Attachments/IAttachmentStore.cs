namespace Vinette.Core.Attachments;

/// <summary>A file the user has uploaded to attach to a message that is not sent yet.</summary>
/// <param name="Id">Opaque identifier (22 characters of URL-safe text); the message that is sent names its attachments by it.</param>
/// <param name="FileName">The name the recipient will see: already cleaned (no path, no control characters), never empty.</param>
/// <param name="ContentType">The type of the file as it will be sent: a harmless one from the short list, or <c>application/octet-stream</c>.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="UploadedAt">When it was uploaded; it is removed a day (by default) after that if it was not used.</param>
public sealed record StoredAttachment(string Id, string FileName, string ContentType, long Size, DateTimeOffset UploadedAt);

/// <summary>What may be uploaded: each limit applies to one mailbox.</summary>
/// <param name="MaxFileBytes">The biggest single file.</param>
/// <param name="MaxTotalBytes">The most all the files that wait to be sent may take together.</param>
/// <param name="MaxFiles">The most files that wait to be sent.</param>
public sealed record AttachmentLimits(long MaxFileBytes, long MaxTotalBytes, int MaxFiles);

/// <summary>Why an upload was refused.</summary>
public enum AttachmentRefusal
{
    /// <summary>The file is bigger than <see cref="AttachmentLimits.MaxFileBytes"/>.</summary>
    FileTooLarge,

    /// <summary>With this file the waiting ones would be more than <see cref="AttachmentLimits.MaxTotalBytes"/>.</summary>
    TotalTooLarge,

    /// <summary>The mailbox already has <see cref="AttachmentLimits.MaxFiles"/> files waiting.</summary>
    TooManyFiles,
}

/// <summary>An upload was refused for one of the limits; nothing of the file was kept.</summary>
public sealed class AttachmentRefusedException(AttachmentRefusal reason) : Exception($"The attachment was refused: {reason}.")
{
    public AttachmentRefusal Reason { get; } = reason;
}

/// <summary>A stored file with its content. Dispose it to close the file.</summary>
public sealed class StoredAttachmentContent(StoredAttachment attachment, Stream content) : IAsyncDisposable
{
    public StoredAttachment Attachment { get; } = attachment;

    /// <summary>The bytes of the file, positioned at the start.</summary>
    public Stream Content { get; } = content;

    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>
/// Where files wait between being uploaded and being sent with a message (VIN-093). Everything here is untrusted bytes: files are
/// kept under random identifiers, belong to the mailbox that uploaded them (nobody else can see or use one, whatever its id), are never
/// run or served back as pages, and are removed after sending, when the user takes them off, and after <c>Attachments:Lifetime</c>.
/// </summary>
public interface IAttachmentStore
{
    /// <summary>The limits uploads are held to.</summary>
    AttachmentLimits Limits { get; }

    /// <summary>
    /// Keeps the file read from <paramref name="content"/> for <paramref name="owner"/> (the mailbox address). The content is read
    /// as it comes, never held whole in memory, and reading stops as soon as a limit is passed; nothing is kept then or when the
    /// upload breaks off. Throws <see cref="AttachmentRefusedException"/> for a limit.
    /// </summary>
    Task<StoredAttachment> SaveAsync(string owner, string fileName, string? contentType, Stream content, CancellationToken cancellationToken);

    /// <summary>The files that wait for <paramref name="owner"/>, oldest first.</summary>
    Task<IReadOnlyList<StoredAttachment>> ListAsync(string owner, CancellationToken cancellationToken);

    /// <summary>One file of <paramref name="owner"/> with its bytes, or null if there is no such file (or it is somebody else's).</summary>
    Task<StoredAttachmentContent?> OpenAsync(string owner, string id, CancellationToken cancellationToken);

    /// <summary>Removes one file of <paramref name="owner"/>; false if there was none.</summary>
    Task<bool> RemoveAsync(string owner, string id, CancellationToken cancellationToken);

    /// <summary>Removes the files that have waited longer than the lifetime, and what an upload that broke off left behind. Returns how many.</summary>
    Task<int> RemoveExpiredAsync(CancellationToken cancellationToken);
}
