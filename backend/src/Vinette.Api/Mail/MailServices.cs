using Microsoft.Extensions.Options;
using Vinette.Api.Configuration;
using Vinette.Core.Authentication;
using Vinette.Core.Mail;
using Vinette.Mail;

namespace Vinette.Api.Mail;

public static class MailServices
{
    /// <summary>Wires the mail server access from Vinette.Mail to the validated Imap options.</summary>
    public static IServiceCollection AddVinetteMail(this IServiceCollection services)
    {
        services.AddSingleton<IAuthenticator>(provider =>
        {
            var imap = provider.GetRequiredService<IOptions<ImapOptions>>().Value;
            return new ImapAuthenticator(
                new ImapConnectionSettings(imap.Host, imap.Port, imap.Security, imap.Timeout, imap.AllowInvalidCertificate));
        });
        services.AddSingleton<IMailSessionFactory>(provider =>
        {
            var imap = provider.GetRequiredService<IOptions<ImapOptions>>().Value;
            return new ImapMailSessionFactory(
                new ImapConnectionSettings(imap.Host, imap.Port, imap.Security, imap.Timeout, imap.AllowInvalidCertificate));
        });
        services.AddSingleton<MailConnectionManager>();
        services.AddSingleton<IMailGateway>(provider => provider.GetRequiredService<MailConnectionManager>());
        return services;
    }
}
