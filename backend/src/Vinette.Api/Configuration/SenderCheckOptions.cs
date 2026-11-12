namespace Vinette.Api.Configuration;

/// <summary>Bound to the "SenderCheck" section.</summary>
public sealed class SenderCheckOptions
{
    public const string SectionName = "SenderCheck";

    /// <summary>
    /// The names our own mail server puts at the start of its <c>Authentication-Results</c> header (RFC 8601 "authserv-id";
    /// usually the server's host name, <c>myhostname</c> in Postfix). Only a header with one of these names, on top of the
    /// message, can make a sender "verified". Empty means the address of the mail server (<c>Imap:Host</c>).
    /// </summary>
    public string[] TrustedAuthservIds { get; set; } = [];
}
