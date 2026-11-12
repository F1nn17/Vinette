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

    [Theory]
    [InlineData("10")]
    [InlineData("999999")]
    [InlineData("9000000000")]
    public void A_Mail_Size_Limit_Outside_Its_Range_Fails_Startup(string bytes)
    {
        StartupFailure.AssertOptionsInvalid(b => b.UseSetting("Imap:MaxViewableMessageBytes", bytes));
    }

    [Theory]
    [InlineData("ExternalImages:AllowedPorts:0", "0")]
    [InlineData("ExternalImages:AllowedPorts:0", "70000")]
    [InlineData("ExternalImages:MaxImageBytes", "5")]
    [InlineData("ExternalImages:MaxImages", "0")]
    [InlineData("ExternalImages:RequestTimeout", "00:00:00")]
    [InlineData("ExternalImages:RequestTimeout", "00:10:00")]
    [InlineData("ExternalImages:MaxTotalBytes", "2000")]
    public void External_Image_Limits_Outside_Their_Range_Fail_Startup(string key, string value)
    {
        StartupFailure.AssertOptionsInvalid(b => b.UseSetting(key, value));
    }

    [Fact]
    public void External_Images_Are_Fetched_From_Public_Addresses_On_Web_Ports_By_Default()
    {
        using var factory = new WebApplicationFactory<Program>();

        var options = factory.Services.GetRequiredService<IOptions<ExternalImagesOptions>>().Value;

        Assert.True(options.Enabled);
        Assert.False(options.AllowPrivateAddresses);
        Assert.Equal([80, 443], options.AllowedPorts);
        Assert.Equal(2_000_000, options.MaxImageBytes);
        Assert.Equal(10_000_000, options.MaxTotalBytes);
        Assert.Equal(30, options.MaxImages);
        Assert.Equal(TimeSpan.FromSeconds(5), options.RequestTimeout);
    }

    [Fact]
    public void An_Unknown_Settings_Store_Fails_Startup()
    {
        // A value that is not a store at all cannot even be read into the option: the application does not start either way.
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Settings:Store", "Carrier pigeon"));

        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    [Fact]
    public void The_Settings_Store_Is_Registered_In_DI_And_Uses_The_Configured_Directory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vinette-settings-di-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Settings:Path", directory));

            var store = factory.Services.GetRequiredService<Vinette.Core.Settings.IUserSettingsStore>();

            Assert.IsType<Vinette.Api.Settings.FileUserSettingsStore>(store);
            Assert.True(Directory.Exists(directory));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
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
    public void Redis_Store_Without_A_Connection_String_Fails_Startup()
    {
        StartupFailure.AssertOptionsInvalid(b => b.UseSetting("Session:Store", "Redis"));
    }

    [Fact]
    public void Sessions_Are_Kept_In_Memory_Unless_Redis_Is_Configured()
    {
        using var factory = new WebApplicationFactory<Program>();

        Assert.IsType<Vinette.Api.Sessions.InMemorySessionStore>(factory.Services.GetRequiredService<Vinette.Core.Sessions.ISessionStore>());
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
