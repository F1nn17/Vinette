using Vinette.Core;
using Vinette.Core.Authentication;
using Vinette.TestSupport;

namespace Vinette.Mail.Tests;

/// <summary>The authenticator against a real Dovecot (self-signed certificate, see DovecotFixture).</summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class ImapAuthenticatorIntegrationTests(DovecotFixture dovecot)
{
    private static readonly ClientInfo Client = new("203.0.113.7", "Vinette tests");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private ImapAuthenticator Create(ConnectionSecurity security, int port, bool allowInvalidCertificate = false) =>
        new(new ImapConnectionSettings(dovecot.Host, port, security, TimeSpan.FromSeconds(20), allowInvalidCertificate));

    private static LoginRequest Login(MailAccount account, string? password = null) =>
        new(account.Address, password ?? account.Password);

    [Fact]
    public async Task Valid_Credentials_Succeed()
    {
        var result = await Create(ConnectionSecurity.None, dovecot.ImapPort)
            .AuthenticateAsync(Login(DovecotFixture.Alice), Client, Token);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Wrong_Password_Is_Rejected()
    {
        var result = await Create(ConnectionSecurity.None, dovecot.ImapPort)
            .AuthenticateAsync(Login(DovecotFixture.Alice, "wrong-password"), Client, Token);

        Assert.Equal(AuthenticationOutcome.InvalidCredentials, result.Outcome);
    }

    [Fact]
    public async Task Unknown_User_Is_Rejected_The_Same_Way()
    {
        var result = await Create(ConnectionSecurity.None, dovecot.ImapPort)
            .AuthenticateAsync(new LoginRequest("nobody@vinette.test", "whatever"), Client, Token);

        Assert.Equal(AuthenticationOutcome.InvalidCredentials, result.Outcome);
    }

    [Fact]
    public async Task Unreachable_Server_Is_A_Separate_Outcome()
    {
        var closed = new ImapAuthenticator(
            new ImapConnectionSettings(dovecot.Host, 1, ConnectionSecurity.None, TimeSpan.FromSeconds(5)));

        var result = await closed.AuthenticateAsync(Login(DovecotFixture.Alice), Client, Token);

        Assert.Equal(AuthenticationOutcome.ServerUnavailable, result.Outcome);
    }

    [Theory]
    [InlineData(ConnectionSecurity.Tls)]
    [InlineData(ConnectionSecurity.StartTls)]
    public async Task Self_Signed_Certificate_Is_Rejected_By_Default(ConnectionSecurity security)
    {
        var port = security == ConnectionSecurity.Tls ? dovecot.ImapsPort : dovecot.ImapPort;

        var result = await Create(security, port).AuthenticateAsync(Login(DovecotFixture.Alice), Client, Token);

        // Not InvalidCredentials: the password was never sent to a server we could not verify.
        Assert.Equal(AuthenticationOutcome.ServerUnavailable, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Diagnostic));
    }

    [Theory]
    [InlineData(ConnectionSecurity.Tls)]
    [InlineData(ConnectionSecurity.StartTls)]
    public async Task Self_Signed_Certificate_Works_When_Explicitly_Allowed(ConnectionSecurity security)
    {
        var port = security == ConnectionSecurity.Tls ? dovecot.ImapsPort : dovecot.ImapPort;

        var result = await Create(security, port, allowInvalidCertificate: true)
            .AuthenticateAsync(Login(DovecotFixture.Bob), Client, Token);

        Assert.True(result.IsSuccess);
    }
}
