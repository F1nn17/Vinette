using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vinette.Api.Configuration;

namespace Vinette.Api.Tests;

public class OptionsValidationTests
{
    [Fact]
    public void Invalid_Accent_Fails_Startup()
    {
        StartupFailure.AssertOptionsInvalid(b => b.UseSetting("Branding:Accent", "red"));
    }

    [Fact]
    public void MaxLifetime_Shorter_Than_IdleTimeout_Fails_Startup()
    {
        StartupFailure.AssertOptionsInvalid(b =>
        {
            b.UseSetting("Session:IdleTimeout", "02:00:00");
            b.UseSetting("Session:MaxLifetime", "01:00:00");
        });
    }

    [Fact]
    public void RememberMeLifetime_Shorter_Than_MaxLifetime_Fails_Startup()
    {
        StartupFailure.AssertOptionsInvalid(b =>
        {
            b.UseSetting("Session:MaxLifetime", "10.00:00:00");
            b.UseSetting("Session:RememberMeLifetime", "5.00:00:00");
        });
    }

    [Fact]
    public void Session_Services_Are_Registered_In_DI()
    {
        using var factory = new WebApplicationFactory<Program>();

        Assert.IsType<Vinette.Api.Sessions.InMemorySessionStore>(factory.Services.GetRequiredService<Vinette.Core.Sessions.ISessionStore>());
        Assert.IsType<Vinette.Api.Sessions.DataProtectionCredentialProtector>(factory.Services.GetRequiredService<Vinette.Core.Sessions.ICredentialProtector>());
        Assert.NotNull(factory.Services.GetRequiredService<Vinette.Api.Sessions.SessionService>());
    }

    [Fact]
    public void Non_Positive_Imap_Timeout_Fails_Startup()
    {
        StartupFailure.AssertOptionsInvalid(b => b.UseSetting("Imap:Timeout", "00:00:00"));
    }

    [Fact]
    public void Authenticator_Is_Registered_In_DI()
    {
        using var factory = new WebApplicationFactory<Program>();

        var authenticator = factory.Services.GetRequiredService<Vinette.Core.Authentication.IAuthenticator>();

        Assert.IsType<Vinette.Mail.ImapAuthenticator>(authenticator);
    }

    [Fact]
    public void Environment_Variables_With_Prefix_Override_Settings()
    {
        Environment.SetEnvironmentVariable("VINETTE_Branding__Name", "Acme Mail");
        try
        {
            using var factory = new WebApplicationFactory<Program>();
            var options = factory.Services.GetRequiredService<IOptions<BrandingOptions>>();
            Assert.Equal("Acme Mail", options.Value.Name);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VINETTE_Branding__Name", null);
        }
    }
}
