namespace Vinette.Api.Configuration;

/// <summary>Bound to the "DataProtection" section; named to avoid clashing with the framework's DataProtectionOptions.</summary>
public sealed class KeyStorageOptions
{
    public const string SectionName = "DataProtection";

    /// <summary>Directory for Data Protection keys. Must be persistent in production. Empty uses the framework default.</summary>
    public string? KeysPath { get; set; }

    /// <summary>
    /// A PKCS#12 file (.pfx) with a certificate and its private key. When set, the keys in <see cref="KeysPath"/> are written encrypted
    /// with it and read back with it, so that a copy of the directory alone (a backup, a stray volume) opens nothing. Empty leaves the
    /// keys readable by whoever can read the directory. If it is set and cannot be used, the application does not start.
    /// </summary>
    public string? CertificatePath { get; set; }

    /// <summary>A file with the password of the certificate (a Docker or Kubernetes secret). Wins over <see cref="CertificatePassword"/>.</summary>
    public string? CertificatePasswordFile { get; set; }

    /// <summary>The password of the certificate, if it is given as a setting or an environment variable. Empty for a file without a password.</summary>
    public string? CertificatePassword { get; set; }

    /// <summary>
    /// Earlier certificates, for changing the certificate: the keys that were written with them can still be read, but nothing new is
    /// written with them. Each is a .pfx file; they share the password of the current one.
    /// </summary>
    public string[] PreviousCertificatePaths { get; set; } = [];
}
