using Vinette.Api.Auth;
using Vinette.Api.Sessions;
using Vinette.Core.Mail;

namespace Vinette.Api.Mail;

public static class MailRequestExtensions
{
    /// <summary>
    /// The mail credentials of the signed-in user of this request, for <see cref="IMailGateway"/>. The password is decrypted
    /// here for the connection only: it is never logged or returned.
    /// </summary>
    public static MailCredentials GetMailCredentials(this HttpContext context, SessionService sessions)
    {
        var session = context.GetSession() ?? throw new InvalidOperationException("The request has no session; the endpoint must require authentication.");
        return new MailCredentials(session.Id, session.Address, sessions.RevealPassword(session), session.ClientIp);
    }
}
