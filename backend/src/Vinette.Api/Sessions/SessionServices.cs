using Vinette.Core.Sessions;

namespace Vinette.Api.Sessions;

public static partial class SessionServices
{
    /// <summary>In-memory sessions with the password encrypted by Data Protection and periodic cleanup.</summary>
    public static IServiceCollection AddVinetteSessions(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ICredentialProtector, DataProtectionCredentialProtector>();
        services.AddSingleton<ISessionStore, InMemorySessionStore>();
        services.AddSingleton<SessionService>();
        services.AddHostedService<SessionCleanupService>();
        return services;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "DataProtection:KeysPath is not set: encryption keys are not persisted, so every user is signed out when the application restarts. Set it to a persistent directory.")]
    public static partial void LogKeysNotPersisted(ILogger logger);
}
