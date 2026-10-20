using Vinette.Core.Authentication;
using Vinette.Core.Sessions;

namespace Vinette.Core.Tests;

public class AuthenticationContractTests
{
    [Fact]
    public void LoginRequest_ToString_Does_Not_Leak_The_Password()
    {
        var text = new LoginRequest("alice@vinette.test", "s3cret-pass").ToString();

        Assert.Contains("alice@vinette.test", text, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret-pass", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Session_ToString_Does_Not_Leak_Secrets()
    {
        var id = SessionId.Create();
        var session = new Session(id, "alice@vinette.test", new byte[] { 1, 2, 3 }, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, false, null);

        var text = session.ToString();

        Assert.DoesNotContain(id, text, StringComparison.Ordinal);
        Assert.Contains("alice@vinette.test", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionId_Is_Unique_UrlSafe_And_43_Characters()
    {
        var ids = Enumerable.Range(0, 1000).Select(_ => SessionId.Create()).ToList();

        Assert.Equal(1000, ids.Distinct().Count());
        Assert.All(ids, id =>
        {
            Assert.Equal(SessionId.Length, id.Length);
            Assert.True(SessionId.IsWellFormed(id));
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("has spaces and is exactly forty-three chars!!")]
    [InlineData("../../etc/passwd-padding-padding-padding-padd")]
    public void SessionId_Rejects_Malformed_Values(string? value)
    {
        Assert.False(SessionId.IsWellFormed(value));
    }

    [Fact]
    public void AuthenticationResult_Exposes_Outcomes()
    {
        Assert.True(AuthenticationResult.Success.IsSuccess);
        Assert.False(AuthenticationResult.InvalidCredentials.IsSuccess);
        Assert.False(AuthenticationResult.ServerUnavailable.IsSuccess);
        Assert.Equal(AuthenticationOutcome.InvalidCredentials, AuthenticationResult.InvalidCredentials.Outcome);
    }

    [Fact]
    public void Core_Does_Not_Depend_On_MailKit_Or_AspNetCore()
    {
        var references = typeof(Session).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        Assert.DoesNotContain(references, name =>
            name.StartsWith("MailKit", StringComparison.Ordinal)
            || name.StartsWith("MimeKit", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }
}
