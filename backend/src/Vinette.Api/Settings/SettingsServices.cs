using Microsoft.Extensions.Options;
using Vinette.Api.Configuration;
using Vinette.Core.Settings;

namespace Vinette.Api.Settings;

public static partial class SettingsServices
{
    /// <summary>The store of user settings chosen by <c>Settings:Store</c>.</summary>
    public static IServiceCollection AddVinetteSettings(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IUserSettingsStore>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<SettingsOptions>>().Value;
            if (string.IsNullOrWhiteSpace(options.Path))
            {
                LogPathNotSet(sp.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SettingsServices).FullName!), FileUserSettingsStore.DefaultDirectory);
            }

            return options.Store switch
            {
                SettingsStoreKind.File => ActivatorUtilities.CreateInstance<FileUserSettingsStore>(sp),
                _ => throw new InvalidOperationException("Unknown settings store."),
            };
        });
        return services;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Settings:Path is not set: user settings are kept in {Directory}. Set it to a persistent directory, or the settings are lost with the container.")]
    private static partial void LogPathNotSet(ILogger logger, string directory);
}
