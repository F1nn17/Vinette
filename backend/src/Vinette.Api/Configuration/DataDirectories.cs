namespace Vinette.Api.Configuration;

/// <summary>
/// The directories the application keeps its data in (the keys, the user settings, the files waiting to be sent, the queue of scheduled messages) are
/// looked at once, when it starts (VIN-142): each is made if it is missing and a small file is written and removed in it. A directory the application
/// cannot use stops the start and names the setting to fix. Without this the application starts "healthy" and the first sign-in or the first saved
/// setting fails minutes later, with an error far from its cause.
/// </summary>
public static partial class DataDirectories
{
    /// <summary>Raised for a directory that cannot be used; the message names the setting and says what to do.</summary>
    public sealed class DataDirectoryException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

    /// <summary>Every directory that is set (a setting left empty means a temporary or default place, which is not looked at).</summary>
    public static IEnumerable<(string Setting, string Path)> Configured(
        KeyStorageOptions keys,
        SettingsOptions settings,
        AttachmentsOptions attachments,
        SchedulingOptions scheduling)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(scheduling);
        var all = new (string Setting, string? Path)[]
        {
            ("DataProtection:KeysPath", keys.KeysPath),
            ("Settings:Path", settings.Path),
            ("Attachments:Path", attachments.Path),
            ("Scheduling:Path", scheduling.Path),
        };
        return all.Where(d => !string.IsNullOrWhiteSpace(d.Path)).Select(d => (d.Setting, d.Path!));
    }

    /// <summary>
    /// The directory for temporary files, when the environment names one (<c>TMPDIR</c>; the Docker image sets it to <c>/data/tmp</c> so that the root file
    /// system can be read-only). On a volume that starts empty (a Kubernetes claim, a bind mount) that directory is not there yet, and the key store
    /// fails on its first write without it, so it is looked at with the others.
    /// </summary>
    public static IEnumerable<(string Setting, string Path)> Temporary(string? tmpdir) =>
        string.IsNullOrWhiteSpace(tmpdir) ? [] : [("TMPDIR", tmpdir)];

    /// <summary>Makes sure every configured directory exists and can be written to; throws <see cref="DataDirectoryException"/> for the first that cannot.</summary>
    public static void EnsureWritable(IEnumerable<(string Setting, string Path)> directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        foreach (var (setting, path) in directories)
        {
            EnsureWritable(setting, path);
        }
    }

    /// <summary>One directory: created if missing, then a file is written and removed.</summary>
    public static void EnsureWritable(string setting, string path)
    {
        try
        {
            _ = Directory.CreateDirectory(path);
            var probe = Path.Combine(path, ".vinette-write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new DataDirectoryException(
                $"{setting}: the directory '{path}' does not exist and cannot be created, or the application cannot write to it ({exception.GetType().Name}). "
                + "Create it and give the user the application runs as write access, or set it to a directory that is writable "
                + "(in the Docker image the data directories are under /data: mount a volume there).",
                exception);
        }
    }

    /// <summary>Says in the log why the application does not start.</summary>
    [LoggerMessage(Level = LogLevel.Critical, Message = "{Problem}")]
    public static partial void LogProblem(ILogger logger, string problem);
}
