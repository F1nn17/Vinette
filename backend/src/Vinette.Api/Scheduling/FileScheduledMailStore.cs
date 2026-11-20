using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Vinette.Api.Configuration;
using Vinette.Core.Mail;
using Vinette.Core.Scheduling;

namespace Vinette.Api.Scheduling;

/// <summary>
/// <see cref="IScheduledMailStore"/> on a directory (ADR-0008): <c>{id}.json</c> describes a message (and holds the encrypted mailbox password
/// while it is needed), <c>{id}.eml</c> is the mail itself. The id is random and is checked before it touches a path, so it can never lead out of
/// the directory. Every write goes to a temporary file that replaces the real one, so a crash leaves nothing half written; the changes of one
/// message are taken one at a time. Files are readable by the user of the process only (Unix).
/// </summary>
public sealed partial class FileScheduledMailStore : IScheduledMailStore
{
    /// <summary>A description larger than this was not written by this store.</summary>
    private const long MaxDescriptionBytes = 256 * 1024;

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex WellFormedId();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string root;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new(StringComparer.Ordinal);

    public FileScheduledMailStore(IOptions<SchedulingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var path = options.Value.Path;
        root = string.IsNullOrWhiteSpace(path) ? DefaultDirectory : System.IO.Path.GetFullPath(path);
    }

    /// <summary>Where the queue is when <c>Scheduling:Path</c> is not set.</summary>
    public static string DefaultDirectory => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vinette-scheduled");

    private string Description(string id) => System.IO.Path.Combine(root, id + ".json");

    private string Mail(string id) => System.IO.Path.Combine(root, id + ".eml");

    private SemaphoreSlim Lock(string id) => locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));

    public async Task AddAsync(ScheduledMail item, ReadOnlyMemory<byte> raw, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        RequireId(item.Id);
        _ = Directory.CreateDirectory(root);
        var gate = Lock(item.Id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The mail first: a description without its mail would be a message that cannot be sent.
            await WriteAtomicAsync(Mail(item.Id), raw, cancellationToken).ConfigureAwait(false);
            await WriteAtomicAsync(Description(item.Id), JsonSerializer.SerializeToUtf8Bytes(item, Json), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = gate.Release();
        }
    }

    public async Task<IReadOnlyList<ScheduledMail>> ListAsync(string mailbox, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(mailbox);
        var all = await ReadAllAsync(cancellationToken).ConfigureAwait(false);
        return [.. all.Where(m => Same(m.Mailbox, mailbox)).OrderBy(m => m.SendAt).ThenBy(m => m.CreatedAt)];
    }

    public async Task<ScheduledMail?> GetAsync(string mailbox, string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(mailbox);
        if (!Valid(id))
        {
            return null;
        }

        var item = await ReadAsync(Description(id), cancellationToken).ConfigureAwait(false);
        return item is not null && Same(item.Mailbox, mailbox) ? item : null;
    }

    public async Task<IReadOnlyList<ScheduledMail>> DueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var all = await ReadAllAsync(cancellationToken).ConfigureAwait(false);
        return [.. all.Where(m => m.State == ScheduledState.Pending && m.DueAt <= now).OrderBy(m => m.DueAt)];
    }

    public async Task<byte[]?> ReadRawAsync(string id, CancellationToken cancellationToken)
    {
        if (!Valid(id) || !File.Exists(Mail(id)))
        {
            return null;
        }

        try
        {
            return await File.ReadAllBytesAsync(Mail(id), cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null; // removed meanwhile
        }
    }

    public async Task<bool> UpdateAsync(ScheduledMail item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        RequireId(item.Id);
        var gate = Lock(item.Id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(Description(item.Id)))
            {
                return false; // cancelled meanwhile: it must not come back
            }

            await WriteAtomicAsync(Description(item.Id), JsonSerializer.SerializeToUtf8Bytes(item, Json), cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _ = gate.Release();
        }
    }

    public async Task<bool> RemoveAsync(string mailbox, string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(mailbox);
        if (!Valid(id))
        {
            return false;
        }

        var gate = Lock(id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var item = await ReadAsync(Description(id), cancellationToken).ConfigureAwait(false);
            if (item is null || !Same(item.Mailbox, mailbox))
            {
                return false;
            }

            TryDelete(Mail(id));
            TryDelete(Description(id)); // the description last: it holds the password, and a mail without it can never be sent
            return true;
        }
        finally
        {
            _ = gate.Release();
        }
    }

    public async Task DropRawAsync(string id, CancellationToken cancellationToken)
    {
        if (!Valid(id))
        {
            return;
        }

        var gate = Lock(id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TryDelete(Mail(id));
        }
        finally
        {
            _ = gate.Release();
        }
    }

    private static bool Valid([NotNullWhen(true)] string? id) => WellFormedId().IsMatch(id ?? string.Empty);

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static void RequireId(string id)
    {
        if (!Valid(id))
        {
            throw new ArgumentException("The id of a scheduled message is 32 hexadecimal digits.", nameof(id));
        }
    }

    private async Task<List<ScheduledMail>> ReadAllAsync(CancellationToken cancellationToken)
    {
        var found = new List<ScheduledMail>();
        if (!Directory.Exists(root))
        {
            return found;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*.json"))
        {
            if (await ReadAsync(file, cancellationToken).ConfigureAwait(false) is { } item)
            {
                found.Add(item);
            }
        }

        return found;
    }

    private static async Task<ScheduledMail?> ReadAsync(string file, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > MaxDescriptionBytes)
            {
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
            var item = JsonSerializer.Deserialize<ScheduledMail>(bytes, Json);
            // A file whose name and id differ was not written by this store.
            return item is not null && System.IO.Path.GetFileNameWithoutExtension(file) == item.Id && WellFormedId().IsMatch(item.Id) ? item : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return null; // damaged or removed meanwhile: not a message
        }
    }

    private static async Task WriteAtomicAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // the next round or the next start removes it
        }
    }
}
