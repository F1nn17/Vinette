using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Vinette.Api.Configuration;

namespace Vinette.Api.Security;

/// <summary>
/// Where the Data Protection keys are kept and how they are protected (VIN-135). The keys encrypt the sessions' mail passwords, so a copy of
/// the key directory next to a copy of the stored sessions would open them: with a certificate the keys on disk are themselves encrypted and the
/// certificate (kept apart, as a secret) is what has to be guarded.
/// </summary>
public static partial class KeyProtectionSetup
{
    /// <summary>Raised for a certificate setting that cannot be used; the message says what to fix (it never contains a password).</summary>
    public sealed class KeyProtectionException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

    /// <summary>Adds the key directory and, if one is configured, the certificate to the Data Protection builder.</summary>
    public static IDataProtectionBuilder UseVinetteKeys(this IDataProtectionBuilder builder, KeyStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.KeysPath))
        {
            if (!string.IsNullOrWhiteSpace(options.CertificatePath))
            {
                throw new KeyProtectionException("DataProtection:CertificatePath needs DataProtection:KeysPath: the certificate protects the keys in that directory.");
            }

            return builder;
        }

        builder.PersistKeysToFileSystem(new DirectoryInfo(options.KeysPath));
        if (string.IsNullOrWhiteSpace(options.CertificatePath))
        {
            return builder;
        }

        var password = ReadPassword(options);
        var current = Load(options.CertificatePath, password, "DataProtection:CertificatePath");
        var previous = options.PreviousCertificatePaths
            .Select((path, index) => Load(path, password, $"DataProtection:PreviousCertificatePaths:{index}"))
            .ToArray();

        builder.ProtectKeysWithCertificate(current);
        if (previous.Length > 0)
        {
            builder.UnprotectKeysWithAnyCertificate([current, .. previous]);
        }

        return builder;
    }

    /// <summary>Says in the log how the keys are kept (once, at start): the warning is what an administrator should not miss.</summary>
    public static void LogStatus(ILogger logger, KeyStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.KeysPath))
        {
            return; // "keys are not persisted" is reported on its own
        }

        if (string.IsNullOrWhiteSpace(options.CertificatePath))
        {
            LogKeysNotEncrypted(logger);
        }
        else
        {
            LogKeysEncrypted(logger, options.CertificatePath, options.PreviousCertificatePaths.Length);
        }
    }

    private static string? ReadPassword(KeyStorageOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.CertificatePasswordFile))
        {
            return string.IsNullOrEmpty(options.CertificatePassword) ? null : options.CertificatePassword;
        }

        try
        {
            // A secret file usually ends with a line break that is not part of the password.
            return File.ReadAllText(options.CertificatePasswordFile).TrimEnd('\r', '\n');
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new KeyProtectionException($"DataProtection:CertificatePasswordFile ({options.CertificatePasswordFile}) cannot be read: {exception.GetType().Name}.", exception);
        }
    }

    private static X509Certificate2 Load(string path, string? password, string setting)
    {
        if (!File.Exists(path))
        {
            throw new KeyProtectionException($"{setting}: the file '{path}' does not exist or cannot be read by the application.");
        }

        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (System.Security.Cryptography.CryptographicException exception)
        {
            // The message of the framework can name the cause (wrong password); it never holds the password itself.
            throw new KeyProtectionException($"{setting}: '{path}' is not a usable .pfx file, or its password is wrong.", exception);
        }

        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new KeyProtectionException($"{setting}: '{path}' has no private key; the certificate must be exported with it (.pfx).");
        }

        return certificate;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "DataProtection:CertificatePath is not set: the keys in DataProtection:KeysPath are stored unencrypted, so whoever can read that directory can read the saved mail passwords of the sessions. Protect the keys with a certificate (see the administrator's guide).")]
    private static partial void LogKeysNotEncrypted(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "The Data Protection keys are encrypted with the certificate {CertificatePath} ({PreviousCount} earlier certificates can still read older keys)")]
    private static partial void LogKeysEncrypted(ILogger logger, string certificatePath, int previousCount);
}
