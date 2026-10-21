namespace Vinette.Mail;

/// <summary>
/// How hard the connection manager tries to reach the mail server again after a lost or failed connection:
/// <see cref="MaxRetries"/> more attempts, waiting <see cref="BaseDelay"/>, then <see cref="Factor"/> times longer each time
/// (0.2 s, 0.6 s, 1.8 s by default), each wait spread by up to <see cref="Jitter"/> so that many users do not reconnect in lockstep.
/// </summary>
/// <param name="MaxRetries">Attempts after the first one; 0 turns retrying off.</param>
/// <param name="BaseDelay">Wait before the first retry.</param>
/// <param name="Factor">Growth of the wait between retries.</param>
/// <param name="Jitter">Random spread of every wait, 0.2 = plus or minus 20 percent.</param>
public sealed record MailReconnectPolicy(int MaxRetries, TimeSpan BaseDelay, double Factor, double Jitter)
{
    public static MailReconnectPolicy Default { get; } = new(3, TimeSpan.FromMilliseconds(200), 3, 0.2);

    /// <summary>The wait before retry number <paramref name="retry"/> (1 = the first retry).</summary>
    public TimeSpan DelayBefore(int retry)
    {
        var seconds = BaseDelay.TotalSeconds * Math.Pow(Factor, retry - 1);
        var spread = 1 + (((Random.Shared.NextDouble() * 2) - 1) * Jitter);
        return TimeSpan.FromSeconds(seconds * spread);
    }
}
