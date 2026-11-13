using System.Collections.Concurrent;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Vinette.Core;
using Vinette.Core.Mail;
using Vinette.TestSupport;

namespace Vinette.Mail.Tests;

/// <summary>Watching the inbox with IMAP IDLE on a real Dovecot: mail that arrives over SMTP, marks, removals, long waits, sharing.</summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class MailboxWatchIntegrationTests(DovecotFixture dovecot)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private ImapMailSessionFactory Factory(TimeSpan? timeout = null) =>
        new(new ImapConnectionSettings(dovecot.Host, dovecot.ImapPort, ConnectionSecurity.None, timeout ?? TimeSpan.FromSeconds(30)));

    private async Task<MailAccount> AccountAsync(int messages = 0)
    {
        var account = await dovecot.CreateAccountAsync($"idl{Guid.NewGuid():N}"[..14], Token);
        for (var i = 0; i < messages; i++)
        {
            await dovecot.SaveFixtureAsync(account, "INBOX", "01-plain-ascii.eml", Token);
        }

        return account;
    }

    private static MailCredentials Credentials(MailAccount account) => new("session", account.Address, account.Password);

    /// <summary>Delivers a mail the way the world does: over SMTP to the mail server, which puts it into the inbox.</summary>
    private async Task DeliverAsync(MailAccount to)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Anna", "anna@vinette.test"));
        message.To.Add(MailboxAddress.Parse(to.Address));
        message.Subject = "Hello from the world";
        message.Body = new TextPart("plain") { Text = "Hi" };
        using var smtp = new SmtpClient { LocalDomain = "client.vinette.test" }; // the mail server wants a fully-qualified HELO name
        await smtp.ConnectAsync(dovecot.Host, dovecot.SmtpPort, SecureSocketOptions.None, Token);
        await smtp.SendAsync(new MailKit.SendRequest(message), Token);
        await smtp.DisconnectAsync(quit: true, Token);
    }

    private sealed class Recorder
    {
        public ConcurrentQueue<MailboxChange> Changes { get; } = new();

        public ValueTask Add(MailboxChange change)
        {
            Changes.Enqueue(change);
            return ValueTask.CompletedTask;
        }

        public async Task WaitForAsync(Func<IEnumerable<MailboxChange>, bool> condition)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            while (!condition(Changes))
            {
                await Task.Delay(100, timeout.Token);
            }
        }
    }

    [Fact]
    public async Task Mail_That_Arrives_Over_SMTP_Is_Reported_While_The_Connection_Idles()
    {
        var account = await AccountAsync();
        await using var session = await Factory().OpenAsync(Credentials(account), Token);
        var recorder = new Recorder();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var watching = session.WatchInboxAsync(recorder.Add, TimeSpan.FromMinutes(25), stop.Token);
        await Task.Delay(1000, Token); // let the IDLE begin

        await DeliverAsync(account);
        await recorder.WaitForAsync(c => c.Any(x => x.Kind == MailboxChangeKind.CountChanged));
        await stop.CancelAsync();

        await watching.WaitAsync(TimeSpan.FromSeconds(30), Token);
        Assert.All(recorder.Changes, c => Assert.Equal("INBOX", c.Folder));
    }

    [Fact]
    public async Task Marks_And_Removals_Made_Elsewhere_Are_Reported()
    {
        var account = await AccountAsync(messages: 2);
        await using var session = await Factory().OpenAsync(Credentials(account), Token);
        var recorder = new Recorder();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var watching = session.WatchInboxAsync(recorder.Add, TimeSpan.FromMinutes(25), stop.Token);
        await Task.Delay(1000, Token);

        await dovecot.DoveadmAsync(Token, "flags", "add", "-u", account.Address, "\\Flagged", "mailbox", "INBOX", "uid", "1");
        await recorder.WaitForAsync(c => c.Any(x => x.Kind == MailboxChangeKind.FlagsChanged));
        await dovecot.DoveadmAsync(Token, "expunge", "-u", account.Address, "mailbox", "INBOX", "uid", "2");
        await recorder.WaitForAsync(c => c.Any(x => x.Kind == MailboxChangeKind.Expunged));
        await stop.CancelAsync();

        await watching.WaitAsync(TimeSpan.FromSeconds(30), Token);
    }

    [Fact]
    public async Task The_Watch_Outlives_The_Reply_Timeout_And_Its_Own_Restarts()
    {
        // Every reply has 3 s; the IDLE is restarted every 2 s and the test waits 8 s: nothing may break, and mail still arrives.
        var account = await AccountAsync();
        await using var session = await Factory(TimeSpan.FromSeconds(3)).OpenAsync(Credentials(account), Token);
        var recorder = new Recorder();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var watching = session.WatchInboxAsync(recorder.Add, TimeSpan.FromSeconds(2), stop.Token);
        await Task.Delay(TimeSpan.FromSeconds(8), Token);
        Assert.False(watching.IsCompleted);

        await DeliverAsync(account);
        await recorder.WaitForAsync(c => c.Any(x => x.Kind == MailboxChangeKind.CountChanged));
        await stop.CancelAsync();

        await watching.WaitAsync(TimeSpan.FromSeconds(30), Token);
    }

    [Fact]
    public async Task The_Watcher_Shares_One_Connection_Between_Listeners_And_Reports_To_Both()
    {
        var account = await AccountAsync();
        await using var watcher = new MailboxWatcher(Factory(), new MailboxWatchSettings(TimeSpan.FromMinutes(25), TimeSpan.FromSeconds(1), 10, TimeSpan.FromSeconds(2)));
        var phone = new Recorder();
        var computer = new Recorder();
        ValueTask<MailCredentials?> Ask(CancellationToken _) => ValueTask.FromResult<MailCredentials?>(Credentials(account));
        using var a = watcher.Subscribe(account.Address, "phone", Ask, phone.Add);
        using var b = watcher.Subscribe(account.Address, "computer", Ask, computer.Add);
        await Task.Delay(2000, Token);
        Assert.Equal(1, watcher.ConnectionCount);

        await DeliverAsync(account);
        await phone.WaitForAsync(c => c.Any(x => x.Kind == MailboxChangeKind.CountChanged));
        await computer.WaitForAsync(c => c.Any(x => x.Kind == MailboxChangeKind.CountChanged));

        Assert.Equal(1, watcher.ConnectionCount);
    }

    [Fact]
    public async Task A_Password_That_Was_Changed_Drops_The_Listener_And_Opens_Nothing()
    {
        var account = await AccountAsync();
        await using var watcher = new MailboxWatcher(Factory(), new MailboxWatchSettings(TimeSpan.FromMinutes(25), TimeSpan.FromSeconds(1), 10, TimeSpan.FromSeconds(1)));
        var wrong = new MailCredentials("session", account.Address, "not-the-password");

        using var _ = watcher.Subscribe(account.Address, "session", _ => ValueTask.FromResult<MailCredentials?>(wrong), new Recorder().Add);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        while (watcher.WatchCount > 0)
        {
            await Task.Delay(100, timeout.Token);
        }

        Assert.Equal(0, watcher.ConnectionCount);
    }
}
