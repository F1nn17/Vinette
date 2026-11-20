using Microsoft.Extensions.Options;
using Vinette.Api.Configuration;

namespace Vinette.Api.Scheduling;

/// <summary>
/// Looks at the queue every <c>Scheduling:PollInterval</c> and sends the messages whose time has come. Runs on one instance only
/// (<c>Scheduling:Enabled</c>): two senders would send a message twice. Looks at the start too, so that messages that came due while the
/// application was stopped go at once.
/// </summary>
public sealed partial class ScheduledMailSender(ScheduledMailService service, IOptions<SchedulingOptions> options, ILogger<ScheduledMailSender> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            Disabled(logger);
            return;
        }

        using var timer = new PeriodicTimer(options.Value.PollInterval);
        try
        {
            do
            {
                await RoundAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task RoundAsync(CancellationToken cancellationToken)
    {
        try
        {
            _ = await service.SendDueAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            RoundFailed(logger, exception.GetType().Name); // the next round tries again
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduling:Enabled is off: this instance does not send scheduled messages")]
    private static partial void Disabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Looking at the queue of scheduled messages failed ({Reason}); trying again")]
    private static partial void RoundFailed(ILogger logger, string reason);
}
