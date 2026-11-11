using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Vinette.Api.Configuration;
using Vinette.Core.Settings;

namespace Vinette.Api.Settings;

/// <summary>
/// User settings as files: one JSON document per mailbox in a directory. The file name is the SHA-256 of the lower-cased
/// address, so the directory listing does not show who the users are. Writes go to a temporary file that replaces the
/// document, so a crash never leaves half a file; changes of one mailbox are serialized. Meant for one instance (or
/// instances sharing the directory with a lock-capable file system); several instances need another store.
/// </summary>
public sealed partial class FileUserSettingsStore : IUserSettingsStore, IDisposable
{
    private const int LockStripes = 64;

    /// <summary>A document larger than this is not read: it was not written by this store.</summary>
    private const long MaxDocumentBytes = 256 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string directory;
    private readonly ILogger<FileUserSettingsStore> logger;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim[] locks = [.. Enumerable.Range(0, LockStripes).Select(_ => new SemaphoreSlim(1, 1))];

    public FileUserSettingsStore(IOptions<SettingsOptions> options, ILogger<FileUserSettingsStore> logger, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.logger = logger;
        this.time = time;
        directory = ResolveDirectory(options.Value.Path);
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>The directory used when <c>Settings:Path</c> is empty.</summary>
    internal static string DefaultDirectory { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        "vinette",
        "settings");

    public async Task<UserSettings> GetAsync(string mailbox, CancellationToken cancellationToken)
    {
        var path = PathOf(mailbox, out var gate);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadAsync(path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<UserSettings> UpdateAsync(string mailbox, Func<UserSettings, UserSettings> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        var path = PathOf(mailbox, out var gate);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
            var changed = change(current) ?? throw new InvalidSettingsException("The change produced no settings.");
            if (!changed.IsValid(out var problem))
            {
                throw new InvalidSettingsException(problem!);
            }

            await WriteAsync(path, changed, cancellationToken).ConfigureAwait(false);
            return changed;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteAsync(string mailbox, CancellationToken cancellationToken)
    {
        var path = PathOf(mailbox, out var gate);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            File.Delete(path);
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        foreach (var gate in locks)
        {
            gate.Dispose();
        }
    }

    private static string ResolveDirectory(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? DefaultDirectory : System.IO.Path.GetFullPath(configured);

    private string PathOf(string mailbox, out SemaphoreSlim gate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mailbox);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(mailbox.Trim().ToLowerInvariant()));
        gate = locks[hash[0] % LockStripes];
        return System.IO.Path.Combine(directory, Convert.ToHexStringLower(hash) + ".json");
    }

    private async Task<UserSettings> ReadAsync(string path, CancellationToken cancellationToken)
    {
        FileInfo info = new(path);
        if (!info.Exists)
        {
            return UserSettings.Default;
        }

        try
        {
            if (info.Length > MaxDocumentBytes)
            {
                throw new JsonException("The document is too large.");
            }

            await using var stream = File.OpenRead(path);
            var settings = await JsonSerializer.DeserializeAsync<UserSettings>(stream, Json, cancellationToken).ConfigureAwait(false);
            if (settings is null || !settings.IsValid(out _))
            {
                throw new JsonException("The document is not valid settings.");
            }

            return settings;
        }
        catch (JsonException)
        {
            // Not what this store wrote (damaged, or edited by hand). It is kept aside for the administrator and the user
            // starts with the defaults; neither the name nor the content goes to the log.
            Quarantine(path);
            return UserSettings.Default;
        }
    }

    private void Quarantine(string path)
    {
        var aside = $"{path}.damaged-{time.GetUtcNow().ToUnixTimeSeconds()}";
        try
        {
            File.Move(path, aside, overwrite: true);
        }
        catch (IOException)
        {
            // It stays where it is and is replaced by the next save; the user starts with the defaults either way.
        }

        LogDamaged();
    }

    private static async Task WriteAsync(string path, UserSettings settings, CancellationToken cancellationToken)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, settings, Json, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A settings file could not be read and was set aside; the user starts with the defaults")]
    private partial void LogDamaged();
}
