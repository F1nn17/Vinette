using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Vinette.TestSupport;

/// <summary>A test mailbox. The login is the full address, as on a real Dovecot.</summary>
public sealed record MailAccount(string Address, string Password);

/// <summary>
/// A real Dovecot 2.4 + Postfix (docker-mailserver) in a container with the mailboxes from
/// deploy/docker/dev-mail and the fixture emails from tests/fixtures loaded into both INBOXes.
/// Share it between test classes with a collection fixture (see <see cref="CollectionName"/>).
/// The first run pulls a ~1 GB image and the container needs about a minute to start.
/// </summary>
public sealed class DovecotFixture : IAsyncLifetime
{
    public const string CollectionName = "Dovecot";
    public const string Image = "ghcr.io/docker-mailserver/docker-mailserver:16.0.1";
    public const string Domain = "vinette.test";

    private const int ImapContainerPort = 143;
    private const int ImapsContainerPort = 993;
    private const int SubmissionContainerPort = 587;
    private const int SmtpContainerPort = 25;
    private const string FixturesInContainer = "/vinette-fixtures";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private IContainer? container;

    public static MailAccount Alice { get; } = new($"alice@{Domain}", "alice-pass");

    public static MailAccount Bob { get; } = new($"bob@{Domain}", "bob-pass");

    /// <summary>Load tests/fixtures/*.eml into the INBOX of alice and bob on start (default: yes).</summary>
    public bool SeedFixtures { get; init; } = true;

    /// <summary>Number of fixture emails loaded into each INBOX.</summary>
    public int SeededMessageCount { get; private set; }

    /// <summary>Thumbprint of the generated self-signed certificate; clients should pin to it instead of trusting everything.</summary>
    public string CertificateThumbprint { get; private set; } = string.Empty;

    public string Host => Container.Hostname;

    /// <summary>Plain IMAP (dev only: the container relaxes ssl=required).</summary>
    public int ImapPort => Container.GetMappedPublicPort(ImapContainerPort);

    /// <summary>Implicit-TLS IMAP with a self-signed certificate; clients must accept it.</summary>
    public int ImapsPort => Container.GetMappedPublicPort(ImapsContainerPort);

    /// <summary>Mail submission (STARTTLS, self-signed certificate).</summary>
    public int SubmissionPort => Container.GetMappedPublicPort(SubmissionContainerPort);

    public int SmtpPort => Container.GetMappedPublicPort(SmtpContainerPort);

    private IContainer Container =>
        container ?? throw new InvalidOperationException("The Dovecot fixture has not been initialized.");

    public async ValueTask InitializeAsync()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var fixtures = Path.Combine(baseDirectory, "fixtures");
        var devMail = Path.Combine(baseDirectory, "dev-mail");
        var (certificatePem, keyPem, thumbprint) = CreateSelfSignedCertificate();
        CertificateThumbprint = thumbprint;

        container = new ContainerBuilder(Image)
            .WithHostname("mail.vinette.test")
            .WithEnvironment("OVERRIDE_HOSTNAME", "mail.vinette.test")
            .WithEnvironment("ONE_DIR", "1")
            .WithEnvironment("SSL_TYPE", "manual")
            .WithEnvironment("SSL_CERT_PATH", "/etc/dev-mail-ssl/cert.pem")
            .WithEnvironment("SSL_KEY_PATH", "/etc/dev-mail-ssl/key.pem")
            .WithEnvironment("ENABLE_QUOTAS", "1")
            .WithEnvironment("ENABLE_RSPAMD", "0")
            .WithEnvironment("ENABLE_CLAMAV", "0")
            .WithEnvironment("ENABLE_FAIL2BAN", "0")
            .WithEnvironment("ENABLE_POSTGREY", "0")
            .WithEnvironment("ENABLE_AMAVIS", "0")
            .WithEnvironment("ENABLE_SPAMASSASSIN", "0")
            .WithEnvironment("SPOOF_PROTECTION", "0")
            .WithPortBinding(ImapContainerPort, true)
            .WithPortBinding(ImapsContainerPort, true)
            .WithPortBinding(SubmissionContainerPort, true)
            .WithPortBinding(SmtpContainerPort, true)
            .WithResourceMapping(
                File.ReadAllBytes(Path.Combine(devMail, "postfix-accounts.cf")),
                "/tmp/docker-mailserver/postfix-accounts.cf")
            .WithResourceMapping(
                File.ReadAllBytes(Path.Combine(devMail, "dovecot.cf")),
                "/tmp/docker-mailserver/dovecot.cf")
            .WithResourceMapping(Encoding.ASCII.GetBytes(certificatePem), "/etc/dev-mail-ssl/cert.pem")
            .WithResourceMapping(Encoding.ASCII.GetBytes(keyPem), "/etc/dev-mail-ssl/key.pem")
            .WithResourceMapping(new DirectoryInfo(fixtures), FixturesInContainer)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted(
                "sh", "-c", "ss -lnt | grep -q ':143 ' && ss -lnt | grep -q ':587 '"))
            .Build();

        await container.StartAsync(TestContext.Current.CancellationToken);

        if (SeedFixtures)
        {
            await SeedFixtureEmailsAsync(fixtures);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (container is not null)
        {
            await container.DisposeAsync();
        }
    }

    /// <summary>Loads the fixture emails byte-for-byte with doveadm (no MIME re-serialization).</summary>
    private async Task SeedFixtureEmailsAsync(string fixturesDirectory)
    {
        var manifest = JsonSerializer.Deserialize<List<ManifestEntry>>(
            await File.ReadAllTextAsync(Path.Combine(fixturesDirectory, "manifest.json"), TestContext.Current.CancellationToken),
            JsonOptions)!;

        foreach (var account in new[] { Alice, Bob })
        {
            foreach (var entry in manifest)
            {
                var result = await Container.ExecAsync(
                    ["sh", "-c", $"doveadm save -u {account.Address} -m INBOX < {FixturesInContainer}/{entry.File}"],
                    TestContext.Current.CancellationToken);

                if (result.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"Seeding {entry.File} into {account.Address} failed (exit {result.ExitCode}): {result.Stderr}");
                }
            }
        }

        SeededMessageCount = manifest.Count;
    }

    private static (string CertificatePem, string KeyPem, string Thumbprint) CreateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=mail.vinette.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("mail.vinette.test");
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());

        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return (certificate.ExportCertificatePem(), rsa.ExportPkcs8PrivateKeyPem(), certificate.Thumbprint);
    }

    private sealed record ManifestEntry(string File);
}
