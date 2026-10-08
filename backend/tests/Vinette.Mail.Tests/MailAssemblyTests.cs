using Vinette.Mail;

namespace Vinette.Mail.Tests;

public class MailAssemblyTests
{
    [Fact]
    public void Exposes_Assembly_Name()
    {
        Assert.Equal("Vinette.Mail", MailAssembly.Name);
    }
}
