using Vinette.Core.Scheduling;

namespace Vinette.Api.Scheduling;

public static class SchedulingServices
{
    /// <summary>The queue of messages that are sent later, the service that works it and the job that sends what is due.</summary>
    public static IServiceCollection AddVinetteScheduling(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IScheduledMailStore, FileScheduledMailStore>();
        services.AddSingleton<ScheduledMailService>();
        services.AddHostedService<ScheduledMailSender>();
        return services;
    }
}
