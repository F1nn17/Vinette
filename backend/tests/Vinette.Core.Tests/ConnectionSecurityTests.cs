using Vinette.Core;

namespace Vinette.Core.Tests;

public class ConnectionSecurityTests
{
    [Fact]
    public void Defines_Tls_StartTls_And_None()
    {
        Assert.Equal(["Tls", "StartTls", "None"], Enum.GetNames<ConnectionSecurity>());
    }
}
