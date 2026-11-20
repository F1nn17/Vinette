namespace Vinette.Core.Mail;

/// <summary>Limits of the suggestions, so that one request cannot ask for something absurd.</summary>
public static class SuggestionLimits
{
    /// <summary>How many suggestions are returned when the caller does not say.</summary>
    public const int DefaultCount = 8;

    /// <summary>The most suggestions one request may ask for.</summary>
    public const int MaxCount = 20;

    /// <summary>The longest text that is looked for, in characters.</summary>
    public const int MaxQueryLength = 100;
}

/// <summary>What is known of one address among the people the mailbox deals with.</summary>
/// <param name="Address">The address, as asked.</param>
/// <param name="LastMail">When the newest message with this person (written to or received from) is dated; null if the person is not among the mailbox's correspondents.</param>
public sealed record KnownRecipient(string Address, DateTimeOffset? LastMail);

/// <summary>
/// Addresses to offer while the user types a recipient: the people the mailbox has written to (the "Sent" folder) and received from (the
/// inbox), the ones written to and the recent ones first. Learned from the newest mail only, and remembered per mailbox for a while, so that
/// an answer does not wait for the mail server however big the mailbox is.
/// </summary>
public interface IRecipientSuggestions
{
    /// <summary>
    /// The people that match <paramref name="query"/> (the beginning of an address, of a name or of a word of a name; anywhere in an address
    /// ranks lower), best first, at most <paramref name="count"/>. An empty query gives the people the mailbox deals with most. The mailbox's own
    /// address is never offered.
    /// </summary>
    /// <param name="credentials">The mailbox.</param>
    /// <param name="query">What has been typed so far; may be empty.</param>
    /// <param name="count">How many at most, 1 to <see cref="SuggestionLimits.MaxCount"/>.</param>
    Task<IReadOnlyList<MailAddressInfo>> SuggestAsync(MailCredentials credentials, string query, int count, CancellationToken cancellationToken);

    /// <summary>
    /// For each address, in the order given, whether the mailbox deals with that person and when the newest message with them is dated. Learned
    /// from the same newest mail as the suggestions, so a person whose mail is older than that is reported as not known.
    /// </summary>
    Task<IReadOnlyList<KnownRecipient>> LookupAsync(MailCredentials credentials, IReadOnlyList<string> addresses, CancellationToken cancellationToken);

    /// <summary>Takes addresses a message has just gone to into the remembered ones of the mailbox (no wait for the next refresh).</summary>
    void Remember(string mailbox, IEnumerable<MailAddressInfo> recipients);
}
