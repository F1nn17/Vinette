using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Vinette.TestSupport;

namespace Vinette.Mail.Tests;

[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class DovecotIntegrationTests(DovecotFixture dovecot)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<ImapClient> ConnectAsync(MailAccount account)
    {
        var client = new ImapClient();
        await client.ConnectAsync(dovecot.Host, dovecot.ImapPort, SecureSocketOptions.None, Token);
        await client.AuthenticateAsync(account.Address, account.Password, Token);
        return client;
    }

    [Fact]
    public async Task Login_With_Valid_Credentials_Succeeds()
    {
        using var client = await ConnectAsync(DovecotFixture.Alice);

        Assert.True(client.IsAuthenticated);
        Assert.True(client.Capabilities.HasFlag(
            ImapCapabilities.SpecialUse | ImapCapabilities.Idle | ImapCapabilities.Move
            | ImapCapabilities.QuickResync | ImapCapabilities.Quota | ImapCapabilities.Metadata));
    }

    [Fact]
    public async Task Login_With_Wrong_Password_Fails()
    {
        using var client = new ImapClient();
        await client.ConnectAsync(dovecot.Host, dovecot.ImapPort, SecureSocketOptions.None, Token);

        await Assert.ThrowsAsync<AuthenticationException>(() =>
            client.AuthenticateAsync(DovecotFixture.Alice.Address, "wrong-password", Token));
    }

    [Fact]
    public async Task Inbox_Contains_The_Seeded_Fixture_Emails()
    {
        using var client = await ConnectAsync(DovecotFixture.Bob);

        await client.Inbox.OpenAsync(FolderAccess.ReadOnly, Token);

        Assert.True(dovecot.SeededMessageCount > 0);
        Assert.Equal(dovecot.SeededMessageCount, client.Inbox.Count);
    }

    [Fact]
    public async Task Seeded_Emails_Are_Byte_For_Byte_Identical()
    {
        using var client = await ConnectAsync(DovecotFixture.Alice);
        await client.Inbox.OpenAsync(FolderAccess.ReadOnly, Token);

        var original = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "03-koi8r-8bit.eml"), Token);
        var koi8Subject = (await client.Inbox.FetchAsync(0, -1, MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope, Token))
            .Single(m => m.Envelope?.Subject?.Contains("KOI8-R", StringComparison.Ordinal) == true);
        using var stream = await client.Inbox.GetStreamAsync(koi8Subject.UniqueId, cancellationToken: Token);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, Token);

        Assert.Equal(original, memory.ToArray());
    }

    [Fact]
    public async Task Tls_Connection_Works_With_The_Self_Signed_Certificate()
    {
        // Pin to the certificate the fixture generated instead of trusting any certificate.
        using var client = new ImapClient
        {
            ServerCertificateValidationCallback = (_, certificate, _, _) =>
                certificate is not null && new X509Certificate2(certificate).Thumbprint == dovecot.CertificateThumbprint,
        };

        await client.ConnectAsync(dovecot.Host, dovecot.ImapsPort, SecureSocketOptions.SslOnConnect, Token);
        await client.AuthenticateAsync(DovecotFixture.Alice.Address, DovecotFixture.Alice.Password, Token);

        Assert.True(client.IsAuthenticated);
        Assert.True(client.IsSecure);
    }

    [Fact]
    public void Manifest_Is_Available_To_Tests()
    {
        var manifest = Path.Combine(AppContext.BaseDirectory, "fixtures", "manifest.json");

        using var document = JsonDocument.Parse(File.ReadAllText(manifest));

        Assert.Equal(dovecot.SeededMessageCount, document.RootElement.GetArrayLength());
    }
}
