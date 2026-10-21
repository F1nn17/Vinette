using Microsoft.Extensions.Options;

namespace Vinette.Api.Configuration;

public static class ConfigurationExtensions
{
    public const string EnvPrefix = "VINETTE_";
    public const string ConfigFileVariable = "VINETTE_CONFIG";

    /// <summary>
    /// Layers configuration: appsettings.json (added by the host) → VINETTE_ environment variables
    /// (nesting via "__") → optional JSON file at the path from VINETTE_CONFIG.
    /// </summary>
    public static void AddVinetteConfiguration(this ConfigurationManager configuration)
    {
        configuration.AddEnvironmentVariables(EnvPrefix);

        var path = configuration[ConfigFileVariable] ?? Environment.GetEnvironmentVariable(ConfigFileVariable);
        if (!string.IsNullOrWhiteSpace(path))
        {
            configuration.AddJsonFile(path, optional: true, reloadOnChange: false);
        }
    }

    public static IServiceCollection AddVinetteOptions(this IServiceCollection services, IConfiguration configuration)
    {
        AddValidated<ImapOptions>(services, configuration, ImapOptions.SectionName);
        AddValidated<SmtpOptions>(services, configuration, SmtpOptions.SectionName);
        AddValidated<BrandingOptions>(services, configuration, BrandingOptions.SectionName);
        AddValidated<SessionLifetimeOptions>(services, configuration, SessionLifetimeOptions.SectionName);
        AddValidated<RedisOptions>(services, configuration, RedisOptions.SectionName);
        services.AddSingleton<IValidateOptions<RedisOptions>, RedisOptionsValidator>();
        AddValidated<KeyStorageOptions>(services, configuration, KeyStorageOptions.SectionName);
        AddValidated<RequestSecurityOptions>(services, configuration, RequestSecurityOptions.SectionName);
        AddValidated<LoginRateLimitOptions>(services, configuration, LoginRateLimitOptions.SectionName);
        return services;
    }

    private static void AddValidated<T>(IServiceCollection services, IConfiguration configuration, string section)
        where T : class
    {
        services.AddOptions<T>()
            .Bind(configuration.GetSection(section))
            .ValidateDataAnnotations()
            .ValidateOnStart();
    }
}
