namespace Vinette.Core.Mail;

/// <summary>
/// What is needed to act on a mailbox on behalf of a signed-in user. <see cref="SessionId"/> ties the mail connection
/// to the web session; the password is the decrypted one and must not outlive the call (never logged, see <see cref="ToString"/>).
/// </summary>
public sealed record MailCredentials(string SessionId, string Address, string Password)
{
    /// <summary>Redacted: the session id is a bearer secret and the generated ToString of a record would print the password.</summary>
    public override string ToString() => $"MailCredentials {{ Address = {Address}, SessionId = ***, Password = *** }}";
}
