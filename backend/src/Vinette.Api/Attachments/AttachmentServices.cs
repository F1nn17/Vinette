using Vinette.Core.Attachments;

namespace Vinette.Api.Attachments;

public static class AttachmentServices
{
    /// <summary>The store of uploaded files that wait to be sent, and the job that removes the ones that waited too long.</summary>
    public static IServiceCollection AddVinetteAttachments(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IAttachmentStore, FileAttachmentStore>();
        services.AddHostedService<AttachmentCleanupService>();
        return services;
    }
}
