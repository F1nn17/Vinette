using Microsoft.Extensions.Logging.Abstractions;
using Vinette.Api.Mail;
using Vinette.Core;
using Vinette.Core.Mail;
using Vinette.Mail;
using Vinette.TestSupport;

namespace Vinette.Api.Tests;

/// <summary>Send and keep a copy against a real Postfix and Dovecot (VIN-092): the copy is in the "Sent" folder, read, and is the mail that went out.</summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class SentCopyIntegrationTests(DovecotFixture dovecot) : IAsyncDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly List<MailConnectionManager> managers = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var manager in managers)
        {
            await manager.DisposeAsync();
        }
    }

    private async Task<MailAccount> AccountAsync(string kind) =>
        await dovecot.CreateAccountAsync($"{kind}{Guid.NewGuid():N}"[..14], Token);

    private static MailCredentials Credentials(MailAccount account) => new("session-" + account.Address, account.Address, account.Password);

    private (OutgoingMailService Service, MailConnectionManager Gateway) Service()
    {
        var factory = new ImapMailSessionFactory(new ImapConnectionSettings(dovecot.Host, dovecot.ImapPort, ConnectionSecurity.None, TimeSpan.FromSeconds(30)));
        var gateway = new MailConnectionManager(factory);
        managers.Add(gateway);
        var smtp = new SmtpMailSender(new SmtpConnectionSettings(dovecot.Host, dovecot.SubmissionPort, ConnectionSecurity.StartTls, TimeSpan.FromSeconds(30), AllowInvalidCertificate: true));
        return (new OutgoingMailService(new MimeMessageComposer(), smtp, gateway, NullLogger<OutgoingMailService>.Instance), gateway);
    }

    private static OutgoingMessage Message(MailAccount from, MailAccount to, string subject) =>
        new(new MailAddressInfo("Алиса", from.Address), [new MailAddressInfo("", to.Address)], [], [], subject, "Привет!\nКопия должна остаться у меня.", null, []);

    [Fact]
    public async Task The_Sent_Message_Is_Delivered_And_A_Read_Copy_Of_The_Same_Mail_Is_In_The_Sent_Folder()
    {
        var alice = await AccountAsync("snd");
        var bob = await AccountAsync("rcv");
        var (service, gateway) = Service();

        var outcome = await service.SendAsync(Credentials(alice), Message(alice, bob, "Копия в «Отправленных»"), Token);

        Assert.Equal(SentCopyOutcome.Saved, outcome.Copy);
        Assert.NotNull(outcome.CopyId);
        Assert.Equal("Sent", outcome.CopyId.Value.Folder);
        var copy = await gateway.ExecuteAsync(Credentials(alice), (session, token) => session.GetMessageAsync(outcome.CopyId.Value, token), Token);
        Assert.True(copy.Seen); // a sent mail is not "unread"
        Assert.Equal(outcome.Message.Raw.ToArray(), copy.Content); // byte for byte what went out
        var listed = await gateway.ExecuteAsync(Credentials(alice), (session, token) => session.ListMessagesAsync(new MessageListQuery("Sent", null, 50), token), Token);
        var line = Assert.Single(listed.Items);
        Assert.Equal("Копия в «Отправленных»", line.Subject);
        Assert.True(line.Seen);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        while (true) // and it is in bob's inbox too
        {
            var inbox = await gateway.ExecuteAsync(Credentials(bob), (session, token) => session.ListMessagesAsync(new MessageListQuery("INBOX", null, 50), token), timeout.Token);
            if (inbox.Items.Count > 0)
            {
                Assert.Equal("Копия в «Отправленных»", inbox.Items[0].Subject);
                break;
            }

            await Task.Delay(300, timeout.Token);
        }
    }

    [Fact]
    public async Task Two_Messages_Leave_Two_Copies_In_Order_And_The_Sent_Folder_Appears_In_The_Folder_List_With_Its_Counters()
    {
        var alice = await AccountAsync("snd");
        var bob = await AccountAsync("rcv");
        var (service, gateway) = Service();

        _ = await service.SendAsync(Credentials(alice), Message(alice, bob, "Первое"), Token);
        _ = await service.SendAsync(Credentials(alice), Message(alice, bob, "Второе"), Token);

        var folders = await gateway.ExecuteAsync(Credentials(alice), (session, token) => session.GetFoldersAsync(token), Token);
        var sent = Assert.Single(FolderRoleGuesser.Complete(folders), f => f.Role == FolderRole.Sent);
        Assert.Equal(2, sent.Total);
        Assert.Equal(0, sent.Unread); // sent mail is read
    }
}
