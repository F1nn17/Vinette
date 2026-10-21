using Microsoft.Extensions.Options;
using Vinette.Api.Configuration;
using Vinette.Mail;

namespace Vinette.Api.Mail;

/// <summary>Periodically closes mail connections that nobody has used for <c>Imap:ConnectionIdleTimeout</c>.</summary>
public sealed partial class MailConnectionCleanupService(
    MailConnectionManager connections,
    IOptions<ImapOptions> options,
    ILogger<MailConnectionCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan MaxInterval = TimeSpan.FromSeconds(30);

    /// <summary>How often to look: often enough to be punctual, rarely enough to cost nothing.</summary>
    internal TimeSpan Interval => TimeSpan.FromTicks(Math.Min(options.Value.ConnectionIdleTimeout.Ticks / 2, MaxInterval.Ticks));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await CloseIdleAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down; the manager closes what is left when the application stops.
        }
    }

    internal async Task CloseIdleAsync(CancellationToken cancellationToken)
    {
        try
        {
            var closed = await connections.CloseIdleAsync(options.Value.ConnectionIdleTimeout, cancellationToken);
            if (closed > 0)
            {
                LogClosed(closed);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFailed(exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Closed {Count} idle mail connections")]
    private partial void LogClosed(int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Closing idle mail connections failed")]
    private partial void LogFailed(Exception exception);
}
