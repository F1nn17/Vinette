using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Vinette.Api.Configuration;
using Vinette.Core.Attachments;
using Vinette.Core.Mail;

namespace Vinette.Api.Attachments;

/// <summary>
/// <see cref="IAttachmentStore"/> on a directory. Each mailbox has a directory of its own (named by a hash of the address, so no address
/// ever becomes part of a path); in it a file is <c>{id}.data</c> with <c>{id}.json</c> beside it (name, type, size, time). The id is
/// 16 random bytes; it is checked before it touches a path, so it can never lead out of the directory. The bytes are written to
/// <c>{id}.part</c> first and renamed when the upload is whole, so a broken upload leaves nothing that looks like a file. Uploads of one
/// mailbox are taken one at a time, which keeps the limits exact when several are started together.
/// </summary>
public sealed partial class FileAttachmentStore : IAttachmentStore
{
    private const int BufferSize = 81920;

    [GeneratedRegex("^[A-Za-z0-9_-]{22}$", RegexOptions.CultureInvariant)]
    private static partial Regex WellFormedId();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string root;
    private readonly TimeSpan lifetime;
    private readonly TimeProvider time;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new(StringComparer.Ordinal);

    public FileAttachmentStore(IOptions<AttachmentsOptions> options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        var settings = options.Value;
        root = string.IsNullOrWhiteSpace(settings.Path) ? DefaultDirectory : System.IO.Path.GetFullPath(settings.Path);
        lifetime = settings.Lifetime;
        this.time = time;
        Limits = new AttachmentLimits(settings.MaxFileBytes, settings.MaxTotalBytes, settings.MaxFiles);
    }

    /// <summary>Where files wait when <c>Attachments:Path</c> is not set.</summary>
    public static string DefaultDirectory => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vinette-attachments");

    public AttachmentLimits Limits { get; }

    public async Task<StoredAttachment> SaveAsync(string owner, string fileName, string? contentType, Stream content, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        ArgumentNullException.ThrowIfNull(content);
        var directory = OwnerDirectory(owner);
        var gate = locks.GetOrAdd(directory, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ = Directory.CreateDirectory(directory);
            var waiting = await ReadAllAsync(directory, cancellationToken).ConfigureAwait(false);
            if (waiting.Count >= Limits.MaxFiles)
            {
                throw new AttachmentRefusedException(AttachmentRefusal.TooManyFiles);
            }

            var taken = waiting.Sum(a => a.Size);
            var id = NewId();
            var part = System.IO.Path.Combine(directory, id + ".part");
            var data = System.IO.Path.Combine(directory, id + ".data");
            var meta = System.IO.Path.Combine(directory, id + ".json");
            try
            {
                long size = 0;
                await using (var file = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
                {
                    var buffer = new byte[BufferSize];
                    int read;
                    while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        size += read;
                        if (size > Limits.MaxFileBytes)
                        {
                            throw new AttachmentRefusedException(AttachmentRefusal.FileTooLarge);
                        }

                        if (taken + size > Limits.MaxTotalBytes)
                        {
                            throw new AttachmentRefusedException(AttachmentRefusal.TotalTooLarge);
                        }

                        await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }
                }

                var stored = new StoredAttachment(
                    id,
                    AttachmentFileName.Sanitize(fileName, "attachment"),
                    AttachmentContentType.ForDownload(contentType),
                    size,
                    time.GetUtcNow());
                File.Move(part, data);
                await File.WriteAllTextAsync(meta, JsonSerializer.Serialize(stored, Json), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                return stored;
            }
            catch
            {
                // Refused, cancelled, a broken connection or a full disk: nothing of this upload stays.
                TryDelete(part);
                TryDelete(data);
                TryDelete(meta);
                throw;
            }
        }
        finally
        {
            _ = gate.Release();
        }
    }

    public async Task<IReadOnlyList<StoredAttachment>> ListAsync(string owner, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        var directory = OwnerDirectory(owner);
        return Directory.Exists(directory) ? await ReadAllAsync(directory, cancellationToken).ConfigureAwait(false) : [];
    }

    public async Task<StoredAttachmentContent?> OpenAsync(string owner, string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        if (!WellFormedId().IsMatch(id ?? string.Empty))
        {
            return null;
        }

        var directory = OwnerDirectory(owner);
        var info = await ReadAsync(System.IO.Path.Combine(directory, id + ".json"), cancellationToken).ConfigureAwait(false);
        var data = System.IO.Path.Combine(directory, id + ".data");
        if (info is null || !File.Exists(data))
        {
            return null;
        }

        try
        {
            return new StoredAttachmentContent(info, new FileStream(data, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, BufferSize, useAsync: true));
        }
        catch (FileNotFoundException)
        {
            return null; // removed meanwhile
        }
    }

    public Task<bool> RemoveAsync(string owner, string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        cancellationToken.ThrowIfCancellationRequested();
        if (!WellFormedId().IsMatch(id ?? string.Empty))
        {
            return Task.FromResult(false);
        }

        var directory = OwnerDirectory(owner);
        var data = System.IO.Path.Combine(directory, id + ".data");
        var meta = System.IO.Path.Combine(directory, id + ".json");
        var existed = File.Exists(data) || File.Exists(meta);
        TryDelete(data);
        TryDelete(meta);
        return Task.FromResult(existed);
    }

    public Task<int> RemoveExpiredAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            return Task.FromResult(0);
        }

        var cutoff = time.GetUtcNow() - lifetime;
        var removed = 0;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                // By the age of the file itself: this also catches what a broken upload left (a .part, a .data without its .json).
                if (File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime && TryDelete(file) && file.EndsWith(".data", StringComparison.Ordinal))
                {
                    removed++;
                }
            }

            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                try
                {
                    Directory.Delete(directory);
                }
                catch (IOException)
                {
                    // somebody is uploading into it right now: the next round
                }
            }
        }

        return Task.FromResult(removed);
    }

    private string OwnerDirectory(string owner) =>
        System.IO.Path.Combine(root, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(owner.Trim().ToUpperInvariant())))[..32]);

    private static string NewId() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<List<StoredAttachment>> ReadAllAsync(string directory, CancellationToken cancellationToken)
    {
        var all = new List<StoredAttachment>();
        foreach (var meta in Directory.EnumerateFiles(directory, "*.json"))
        {
            if (await ReadAsync(meta, cancellationToken).ConfigureAwait(false) is { } info
                && File.Exists(System.IO.Path.ChangeExtension(meta, ".data")))
            {
                all.Add(info);
            }
        }

        return [.. all.OrderBy(a => a.UploadedAt).ThenBy(a => a.Id, StringComparer.Ordinal)];
    }

    private static async Task<StoredAttachment?> ReadAsync(string meta, CancellationToken cancellationToken)
    {
        try
        {
            var info = JsonSerializer.Deserialize<StoredAttachment>(await File.ReadAllTextAsync(meta, cancellationToken).ConfigureAwait(false), Json);
            return info is not null && WellFormedId().IsMatch(info.Id) ? info : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null; // a file that is not ours, or half written: it is not an attachment
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
