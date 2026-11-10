using System.Diagnostics.Metrics;

namespace Vinette.Mail;

/// <summary>
/// Metrics of the mail connections (System.Diagnostics.Metrics, meter <see cref="MeterName"/>). Tags never carry
/// addresses, session ids or message data: only fixed reasons and outcomes.
/// </summary>
public sealed class MailMetrics : IDisposable
{
    public const string MeterName = "Vinette.Mail";

    private readonly Meter meter = new(MeterName);

    internal Meter Meter => meter;
    private readonly Counter<long> opened;
    private readonly Counter<long> closed;
    private readonly Counter<long> errors;
    private readonly Counter<long> reconnects;
    private readonly Histogram<double> commandDuration;

    public MailMetrics(Func<int> openConnections, Func<int> waitingCommands)
    {
        ArgumentNullException.ThrowIfNull(openConnections);
        ArgumentNullException.ThrowIfNull(waitingCommands);
        _ = meter.CreateObservableGauge("vinette.mail.connections.open", openConnections, "{connection}", "Open mail server connections.");
        _ = meter.CreateObservableGauge("vinette.mail.commands.queued", waitingCommands, "{command}", "Commands waiting for the connection of their session or for a free connection place.");
        opened = meter.CreateCounter<long>("vinette.mail.connections.opened", "{connection}", "Mail server connections opened.");
        closed = meter.CreateCounter<long>("vinette.mail.connections.closed", "{connection}", "Mail server connections closed, by reason (released, idle, lost, cancelled, shutdown).");
        errors = meter.CreateCounter<long>("vinette.mail.errors", "{error}", "Failed mail operations, by kind (unavailable, revoked, busy).");
        reconnects = meter.CreateCounter<long>("vinette.mail.reconnects", "{retry}", "Retries after a lost or failed connection.");
        commandDuration = meter.CreateHistogram<double>("vinette.mail.command.duration", "s", "Time from the start of a command to its end, without the wait in the queue, by outcome (ok, error, cancelled).");
    }

    public void ConnectionOpened() => opened.Add(1);

    public void ConnectionClosed(string reason) => closed.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void Error(string kind) => errors.Add(1, new KeyValuePair<string, object?>("kind", kind));

    public void Reconnect() => reconnects.Add(1);

    public void CommandFinished(TimeSpan duration, string outcome) =>
        commandDuration.Record(duration.TotalSeconds, new KeyValuePair<string, object?>("outcome", outcome));

    public void Dispose() => meter.Dispose();
}
