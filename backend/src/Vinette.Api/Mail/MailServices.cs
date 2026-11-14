using Microsoft.Extensions.Options;
using Vinette.Api.Configuration;
using Vinette.Api.Mail.Images;
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
                new ImapConnectionSettings(imap.Host, imap.Port, imap.Security, imap.Timeout, imap.AllowInvalidCertificate, imap.MaxViewableMessageBytes));
        });
        services.AddSingleton<IMailSessionFactory>(provider =>
        {
            var imap = provider.GetRequiredService<IOptions<ImapOptions>>().Value;
            return new ImapMailSessionFactory(
                new ImapConnectionSettings(imap.Host, imap.Port, imap.Security, imap.Timeout, imap.AllowInvalidCertificate, imap.MaxViewableMessageBytes));
        });
        services.AddSingleton(provider =>
        {
            var imap = provider.GetRequiredService<IOptions<ImapOptions>>().Value;
            return new MailConnectionManager(
                provider.GetRequiredService<IMailSessionFactory>(),
                connectionLimits: new MailConnectionLimits(imap.MaxConnectionsPerUser, imap.MaxConnections, imap.ConnectionWaitTimeout));
        });
        services.AddSingleton<IMailGateway>(provider => provider.GetRequiredService<MailConnectionManager>());
        services.AddSingleton<IMailSender>(provider =>
        {
            var smtp = provider.GetRequiredService<IOptions<SmtpOptions>>().Value;
            return new SmtpMailSender(new SmtpConnectionSettings(smtp.Host, smtp.Port, smtp.Security, smtp.Timeout, smtp.AllowInvalidCertificate));
        });
        services.AddSingleton<IMailboxWatcher>(provider =>
        {
            var imap = provider.GetRequiredService<IOptions<ImapOptions>>().Value;
            return new MailboxWatcher(
                provider.GetRequiredService<IMailSessionFactory>(),
                new MailboxWatchSettings(imap.IdleRestart, imap.IdleGrace, imap.MaxIdleConnections, TimeSpan.FromMinutes(1)));
        });
        services.AddSingleton(provider =>
        {
            var configured = provider.GetRequiredService<IOptions<SenderCheckOptions>>().Value.TrustedAuthservIds;
            var imap = provider.GetRequiredService<IOptions<ImapOptions>>().Value;
            return new SenderVerifier(configured.Length > 0 ? configured : [imap.Host]);
        });
        services.AddSingleton<IMessageParser, MimeMessageParser>();
        services.AddSingleton<IMessageHtmlSanitizer, MessageHtmlSanitizer>();
        services.AddSingleton<IDnsResolver, SystemDnsResolver>();
        services.AddSingleton<IExternalImageFetcher>(provider => new ExternalImageFetcher(
            provider.GetRequiredService<IOptions<ExternalImagesOptions>>(),
            provider.GetRequiredService<IDnsResolver>(),
            provider.GetRequiredService<ILogger<ExternalImageFetcher>>()));
        services.AddHostedService<MailConnectionCleanupService>();
        return services;
    }
}
