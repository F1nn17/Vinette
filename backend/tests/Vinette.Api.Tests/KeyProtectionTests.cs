using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Vinette.Api.Configuration;
using Vinette.Api.Security;

namespace Vinette.Api.Tests;

/// <summary>The Data Protection keys on disk, with and without a certificate (VIN-135).</summary>
public sealed class KeyProtectionTests : IDisposable
{
    private const string Secret = "alice-mail-password";

    private readonly string directory = Path.Combine(Path.GetTempPath(), "vinette-keys-" + Guid.NewGuid().ToString("N"));

    public KeyProtectionTests() => Directory.CreateDirectory(directory);

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private string Keys => Path.Combine(directory, "keys");

    /// <summary>A self-signed certificate with a private key, written as a .pfx; returns the path.</summary>
    private string MakePfx(string name, string? password)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        var path = Path.Combine(directory, name + ".pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, password));
        return path;
    }

    /// <summary>The key ring as an application start makes it: a new provider on the same directory.</summary>
    private static IDataProtector Start(KeyStorageOptions options)
    {
        var services = new ServiceCollection();
        services.AddDataProtection().SetApplicationName("Vinette").UseVinetteKeys(options);
        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>().CreateProtector("test");
    }

    private KeyStorageOptions Options(string? certificate = null, string? password = null, params string[] previous) => new()
    {
        KeysPath = Keys,
        CertificatePath = certificate,
        CertificatePassword = password,
        PreviousCertificatePaths = previous,
    };

    private string KeyFiles() => string.Concat(Directory.GetFiles(Keys, "*.xml").Select(File.ReadAllText));

    [Fact]
    public void Without_A_Certificate_The_Keys_Are_Written_As_They_Are()
    {
        var token = Start(Options()).Protect(Secret);

        Assert.Contains("masterKey", KeyFiles(), StringComparison.Ordinal);
        Assert.Equal(Secret, Start(Options()).Unprotect(token));
    }

    [Fact]
    public void With_A_Certificate_The_Keys_On_Disk_Are_Encrypted_And_Still_Work_After_A_Restart()
    {
        var pfx = MakePfx("one", "pa55");
        var token = Start(Options(pfx, "pa55")).Protect(Secret);

        var files = KeyFiles();
        Assert.DoesNotContain("<masterKey", files, StringComparison.Ordinal);
        Assert.Contains("EncryptedData", files, StringComparison.Ordinal); // XML Encryption: the key is inside an encrypted element
        Assert.Equal(Secret, Start(Options(pfx, "pa55")).Unprotect(token)); // a restart
    }

    [Fact]
    public void A_Copy_Of_The_Key_Directory_Opens_Nothing_Without_The_Certificate()
    {
        var pfx = MakePfx("one", "pa55");
        var token = Start(Options(pfx, "pa55")).Protect(Secret);

        // Someone has the directory (a backup) but not the certificate: the old keys cannot be read, only new ones would be made.
        Assert.ThrowsAny<CryptographicException>(() => Start(Options()).Unprotect(token));
    }

    [Fact]
    public void Another_Certificate_Cannot_Read_The_Keys()
    {
        var token = Start(Options(MakePfx("one", "pa55"), "pa55")).Protect(Secret);

        Assert.ThrowsAny<CryptographicException>(() => Start(Options(MakePfx("two", "pa55"), "pa55")).Unprotect(token));
    }

    [Fact]
    public void A_Certificate_Can_Be_Replaced_When_The_Old_One_Is_Kept_As_Previous()
    {
        var old = MakePfx("old", "pa55");
        var fresh = MakePfx("fresh", "pa55");
        var token = Start(Options(old, "pa55")).Protect(Secret);

        var after = Start(Options(fresh, "pa55", old));

        Assert.Equal(Secret, after.Unprotect(token)); // what the old certificate protected is still read
        var second = after.Protect("another"); // and a new key, if one is made, is written with the new certificate
        Assert.Equal("another", Start(Options(fresh, "pa55", old)).Unprotect(second));
    }

    [Fact]
    public void The_Password_Can_Come_From_A_File_With_A_Line_Break_At_The_End()
    {
        var pfx = MakePfx("one", "pa55");
        var secretFile = Path.Combine(directory, "password");
        File.WriteAllText(secretFile, "pa55\n");
        var options = Options(pfx);
        options.CertificatePasswordFile = secretFile;

        var token = Start(options).Protect(Secret);

        Assert.Equal(Secret, Start(options).Unprotect(token));
    }

    [Fact]
    public void A_Certificate_Without_A_Password_Works()
    {
        var pfx = MakePfx("open", null);

        Assert.Equal(Secret, Start(Options(pfx)).Unprotect(Start(Options(pfx)).Protect(Secret)));
    }

    [Fact]
    public void A_Certificate_That_Cannot_Be_Used_Stops_The_Start_And_The_Message_Names_The_Setting_Not_The_Password()
    {
        var pfx = MakePfx("one", "pa55");
        var missing = Assert.Throws<KeyProtectionSetup.KeyProtectionException>(() => Start(Options(Path.Combine(directory, "none.pfx"))));
        var wrong = Assert.Throws<KeyProtectionSetup.KeyProtectionException>(() => Start(Options(pfx, "not-the-password")));
        var noFile = Options(pfx);
        noFile.CertificatePasswordFile = Path.Combine(directory, "no-such-secret");
        var secretMissing = Assert.Throws<KeyProtectionSetup.KeyProtectionException>(() => Start(noFile));
        var previous = Assert.Throws<KeyProtectionSetup.KeyProtectionException>(() => Start(Options(pfx, "pa55", Path.Combine(directory, "gone.pfx"))));

        Assert.Contains("DataProtection:CertificatePath", missing.Message, StringComparison.Ordinal);
        Assert.Contains("password is wrong", wrong.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not-the-password", wrong.Message, StringComparison.Ordinal);
        Assert.Contains("CertificatePasswordFile", secretMissing.Message, StringComparison.Ordinal);
        Assert.Contains("PreviousCertificatePaths:0", previous.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Certificate_Without_A_Private_Key_Is_Refused()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=public", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var path = Path.Combine(directory, "public.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Cert)); // the certificate alone, no key

        Assert.ThrowsAny<KeyProtectionSetup.KeyProtectionException>(() => Start(Options(path)));
    }

    [Fact]
    public void A_Certificate_Needs_A_Key_Directory()
    {
        var pfx = MakePfx("one", "pa55");
        var options = Options(pfx, "pa55");
        options.KeysPath = null;

        var problem = Assert.Throws<KeyProtectionSetup.KeyProtectionException>(() => Start(options));

        Assert.Contains("KeysPath", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Keys_That_Were_Written_Unencrypted_Are_Still_Read_When_A_Certificate_Is_Added_Later()
    {
        var token = Start(Options()).Protect(Secret); // before the certificate: the sessions of the day it is switched on survive

        var pfx = MakePfx("one", "pa55");

        Assert.Equal(Secret, Start(Options(pfx, "pa55")).Unprotect(token));
    }
}
