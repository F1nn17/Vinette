using Vinette.Core.Mail;

namespace Vinette.Core.Tests;

public sealed class MailContractTests
{
    [Fact]
    public void Credentials_Are_Redacted_In_ToString()
    {
        var text = new MailCredentials("session-secret", "alice@vinette.test", "hunter2").ToString();

        Assert.Contains("alice@vinette.test", text, StringComparison.Ordinal);
        Assert.DoesNotContain("session-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Failures_Keep_Their_Cause()
    {
        var cause = new IOException("reset");

        Assert.Same(cause, new MailUnavailableException("down", cause).InnerException);
        Assert.Same(cause, new MailAccessRevokedException("revoked", cause).InnerException);
    }
}
