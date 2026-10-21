using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Vinette.Api.Configuration;
using Vinette.Core.Sessions;

namespace Vinette.Api.Sessions;

public static partial class SessionServices
{
    /// <summary>In-memory sessions with the password encrypted by Data Protection and periodic cleanup.</summary>
    public static IServiceCollection AddVinetteSessions(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ICredentialProtector, DataProtectionCredentialProtector>();
        services.AddSingleton<InMemorySessionStore>();
        services.AddSingleton<ISessionStore>(sp => sp.GetRequiredService<IOptions<SessionLifetimeOptions>>().Value.Store switch
        {
            SessionStoreKind.Redis => ActivatorUtilities.CreateInstance<RedisSessionStore>(sp),
            _ => sp.GetRequiredService<InMemorySessionStore>(),
        });
        services.AddSingleton<IConnectionMultiplexer>(sp => ConnectRedis(sp.GetRequiredService<IOptions<RedisOptions>>().Value));
        services.AddSingleton<SessionService>();
        services.AddHostedService<SessionCleanupService>();
        return services;
    }

    private static ConnectionMultiplexer ConnectRedis(RedisOptions options)
    {
        var configuration = ConfigurationOptions.Parse(options.ConnectionString!);
        configuration.AbortOnConnectFail = false; // the application starts and keeps retrying while Redis is down
        return ConnectionMultiplexer.Connect(configuration);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "DataProtection:KeysPath is not set: encryption keys are not persisted, so every user is signed out when the application restarts. Set it to a persistent directory.")]
    public static partial void LogKeysNotPersisted(ILogger logger);
}
