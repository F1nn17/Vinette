namespace Vinette.Core.Mail;

/// <summary>Storage use of a mailbox. Sizes are in bytes.</summary>
/// <param name="UsedBytes">Space the messages take.</param>
/// <param name="LimitBytes">The mailbox's limit.</param>
public sealed record MailQuota(long UsedBytes, long LimitBytes)
{
    /// <summary>Used share of the limit, 0 to 1 (more than 1 when the mailbox is over its limit); 0 for a zero limit.</summary>
    public double UsedFraction => LimitBytes <= 0 ? 0 : (double)UsedBytes / LimitBytes;
}
