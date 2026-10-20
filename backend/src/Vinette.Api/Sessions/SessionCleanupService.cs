namespace Vinette.Api.Sessions;

/// <summary>Periodically removes expired sessions.</summary>
public sealed partial class SessionCleanupService(SessionService sessions, ILogger<SessionCleanupService> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await CleanUpAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task CleanUpAsync(CancellationToken cancellationToken)
    {
        try
        {
            var removed = await sessions.RemoveExpiredAsync(cancellationToken);
            if (removed > 0)
            {
                LogRemoved(removed);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFailed(exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Removed {Count} expired sessions")]
    private partial void LogRemoved(int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Removing expired sessions failed")]
    private partial void LogFailed(Exception exception);
}
