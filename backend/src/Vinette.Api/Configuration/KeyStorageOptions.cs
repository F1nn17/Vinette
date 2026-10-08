namespace Vinette.Api.Configuration;

/// <summary>Bound to the "DataProtection" section; named to avoid clashing with the framework's DataProtectionOptions.</summary>
public sealed class KeyStorageOptions
{
    public const string SectionName = "DataProtection";

    /// <summary>Directory for Data Protection keys. Must be persistent in production. Empty uses the framework default.</summary>
    public string? KeysPath { get; set; }
}
