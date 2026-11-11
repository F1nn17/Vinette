using System.ComponentModel.DataAnnotations;

namespace Vinette.Api.Configuration;

/// <summary>Where user settings are kept (<c>Settings:Store</c>).</summary>
public enum SettingsStoreKind
{
    /// <summary>Files in a directory: one instance (or one shared directory); works with any mail server.</summary>
    File,
}

/// <summary>Bound to the "Settings" section.</summary>
public sealed class SettingsOptions : IValidatableObject
{
    public const string SectionName = "Settings";

    /// <summary>Where settings are kept; only <c>File</c> exists so far.</summary>
    public SettingsStoreKind Store { get; set; } = SettingsStoreKind.File;

    /// <summary>
    /// Directory for the settings files of <c>Store = File</c>. Must be persistent in production (the Docker image puts it
    /// in the /data volume). Empty uses a directory under the application data folder of the account the application runs as.
    /// </summary>
    public string? Path { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enum.IsDefined(Store))
        {
            yield return new ValidationResult("Store must be File.", [nameof(Store)]);
        }

        if (Path is not null && Path.Contains('\0', StringComparison.Ordinal))
        {
            yield return new ValidationResult("Path is not a valid directory name.", [nameof(Path)]);
        }
    }
}
