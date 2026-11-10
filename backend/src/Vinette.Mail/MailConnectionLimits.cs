namespace Vinette.Mail;

/// <summary>
/// Ceilings on open mail connections. One connection per web session is already guaranteed; these bound the sum.
/// A command that needs a new connection waits up to <see cref="WaitTimeout"/> for a free place and then fails with a "busy" error.
/// </summary>
/// <param name="MaxPerUser">Open connections of one mailbox (it may be signed in on several devices).</param>
/// <param name="MaxTotal">Open connections of the whole application.</param>
/// <param name="WaitTimeout">How long a command waits for a free place.</param>
public sealed record MailConnectionLimits(int MaxPerUser, int MaxTotal, TimeSpan WaitTimeout)
{
    public static MailConnectionLimits Default { get; } = new(5, 200, TimeSpan.FromSeconds(10));
}
