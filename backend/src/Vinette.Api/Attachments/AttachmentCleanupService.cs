using Vinette.Core.Attachments;

namespace Vinette.Api.Attachments;

/// <summary>Periodically removes the uploaded files that were not sent within <c>Attachments:Lifetime</c>, and what a broken upload left behind.</summary>
public sealed partial class AttachmentCleanupService(IAttachmentStore store, ILogger<AttachmentCleanupService> logger) : BackgroundService
{
    /// <summary>How often to look: a file lives a day by default, so ten minutes is punctual enough and costs nothing.</summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // At the start too: files that outlived a stopped application should not wait for the first tick.
        await RemoveExpiredAsync(stoppingToken);
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RemoveExpiredAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task RemoveExpiredAsync(CancellationToken cancellationToken)
    {
        try
        {
            var removed = await store.RemoveExpiredAsync(cancellationToken);
            if (removed > 0)
            {
                Removed(logger, removed);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Failed(logger, exception.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {Count} uploaded files that were not sent in time")]
    private static partial void Removed(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cleaning up uploaded files failed ({Reason})")]
    private static partial void Failed(ILogger logger, string reason);
}
