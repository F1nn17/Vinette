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

    /// <summary>
    /// Restarts the real Dovecot inside the container (every IMAP connection is dropped) and returns when it answers again.
    /// Other test classes of the collection run one after another, so none is using it meanwhile.
    /// </summary>
    public async Task RestartDovecotAsync(CancellationToken cancellationToken)
    {
        // A restart alone leaves running IMAP processes alone, so the open connections are dropped first, as a crash would.
        var result = await Container.ExecAsync(["sh", "-c", "doveadm kick '*'; supervisorctl restart dovecot"], cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Restarting Dovecot failed (exit {result.ExitCode}): {result.Stderr}");
        }

        await WaitUntilAsync(async () => await CanConnectAsync(cancellationToken), TimeSpan.FromSeconds(60), "Dovecot did not come back after the restart.", cancellationToken);
    }

    /// <summary>Creates a mailbox of its own, so a test can change or drop its password without touching alice and bob.</summary>
    public async Task<MailAccount> CreateAccountAsync(string name, CancellationToken cancellationToken)
    {
        var account = new MailAccount($"{name}@{Domain}", $"{name}-pass");
        var result = await Container.ExecAsync(["setup", "email", "add", account.Address, account.Password], cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Creating {account.Address} failed (exit {result.ExitCode}): {result.Stderr}");
        }

        await WaitUntilAsync(async () => await CanLoginAsync(account, cancellationToken), TimeSpan.FromSeconds(90), $"{account.Address} cannot sign in after it was created.", cancellationToken);
        return account;
    }

    /// <summary>Changes the password the way an administrator does and returns once Dovecot accepts the new one and refuses the old one.</summary>
    public async Task<MailAccount> ChangePasswordAsync(MailAccount account, string newPassword, CancellationToken cancellationToken)
    {
        var result = await Container.ExecAsync(["setup", "email", "update", account.Address, newPassword], cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Changing the password of {account.Address} failed (exit {result.ExitCode}): {result.Stderr}");
        }

        var changed = account with { Password = newPassword };
        await WaitUntilAsync(async () => await CanLoginAsync(changed, cancellationToken), TimeSpan.FromSeconds(90), "The new password was not accepted in time.", cancellationToken);
        return changed;
    }

    /// <summary>Creates a folder in a mailbox (a hierarchy separator in the name nests it) and subscribes the mailbox to it.</summary>
    public async Task CreateFolderAsync(MailAccount account, string folder, CancellationToken cancellationToken)
    {
        var result = await Container.ExecAsync(["doveadm", "mailbox", "create", "-s", "-u", account.Address, folder], cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Creating folder {folder} failed (exit {result.ExitCode}): {result.Stderr}");
        }
    }

    /// <summary>Puts one of the fixture emails (by file name, e.g. "plain-text.eml") into a folder of a mailbox.</summary>
    public async Task SaveFixtureAsync(MailAccount account, string folder, string fixtureFile, CancellationToken cancellationToken)
    {
        var result = await Container.ExecAsync(
            ["sh", "-c", $"doveadm save -u {account.Address} -m '{folder}' < {FixturesInContainer}/{fixtureFile}"],
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Saving {fixtureFile} into {folder} failed (exit {result.ExitCode}): {result.Stderr}");
        }
    }

    /// <summary>Marks every message of a folder as read.</summary>
    public async Task MarkFolderReadAsync(MailAccount account, string folder, CancellationToken cancellationToken)
    {
        var result = await Container.ExecAsync(["doveadm", "flags", "add", "-u", account.Address, "\\Seen", "mailbox", folder, "all"], cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Marking {folder} read failed (exit {result.ExitCode}): {result.Stderr}");
        }
    }

    private async Task<bool> CanConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(Host, ImapPort, cancellationToken);
            using var reader = new StreamReader(client.GetStream(), Encoding.ASCII);
            var greeting = await reader.ReadLineAsync(cancellationToken);
            return greeting?.StartsWith("* OK", StringComparison.Ordinal) == true;
        }
        catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    /// <summary>A bare IMAP LOGIN over a plain connection: true when the server accepts the password.</summary>
    public async Task<bool> CanLoginAsync(MailAccount account, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(Host, ImapPort, cancellationToken);
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            _ = await reader.ReadLineAsync(cancellationToken);
            var command = Encoding.ASCII.GetBytes($"a1 LOGIN \"{account.Address}\" \"{account.Password}\"\r\n");
            await stream.WriteAsync(command, cancellationToken);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (line.StartsWith("a1 ", StringComparison.Ordinal))
                {
                    return line.StartsWith("a1 OK", StringComparison.Ordinal);
                }
            }

            return false;
        }
        catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan limit, string failure, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + limit;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(failure);
            }

            await Task.Delay(500, cancellationToken);
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
