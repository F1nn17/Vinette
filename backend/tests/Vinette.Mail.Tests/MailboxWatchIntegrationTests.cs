using System.Collections.Concurrent;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Vinette.Core;
using Vinette.Core.Mail;
using Vinette.TestSupport;

namespace Vinette.Mail.Tests;

/// <summary>
/// Watching the inbox with IMAP IDLE on a real Dovecot, and what the server's notifications become (VIN-081, VIN-082): mail that
/// arrives over SMTP, marks, removals, counters, bursts, long waits, sharing.
/// </summary>
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

    /// <summary>Appends many mails with one command (MULTIAPPEND), so that the server announces them together, as a burst of mail does.</summary>
    private async Task AppendManyAsync(MailAccount to, int count)
    {
        using var client = new MailKit.Net.Imap.ImapClient();
        await client.ConnectAsync(dovecot.Host, dovecot.ImapPort, SecureSocketOptions.None, Token);
        await client.AuthenticateAsync(to.Address, to.Password, Token);
        var requests = Enumerable.Range(1, count).Select(i =>
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("Burst", "burst@vinette.test"));
            message.To.Add(MailboxAddress.Parse(to.Address));
            message.Subject = $"Burst {i}";
            message.Body = new TextPart("plain") { Text = "Hi" };
            return (MailKit.IAppendRequest)new MailKit.AppendRequest(message);
        }).ToList();
        _ = await client.Inbox.AppendAsync(requests, Token);
        await client.DisconnectAsync(quit: true, Token);
    }

    private sealed class Recorder
    {
        public ConcurrentQueue<MailboxEvent> Events { get; } = new();

        public ValueTask Add(MailboxEvent change)
        {
            Events.Enqueue(change);
            return ValueTask.CompletedTask;
        }

        public IEnumerable<T> Of<T>()
            where T : MailboxEvent => Events.OfType<T>();

        public async Task WaitForAsync(Func<Recorder, bool> condition)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            while (!condition(this))
            {
                await Task.Delay(100, timeout.Token);
            }
        }
    }

    /// <summary>Starts watching and waits until the connection has settled into its first wait.</summary>
    private static async Task<(Recorder Recorder, CancellationTokenSource Stop, Task Watching)> StartAsync(IMailSession session)
    {
        var recorder = new Recorder();
        var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var watching = session.WatchInboxAsync(recorder.Add, TimeSpan.FromMinutes(25), stop.Token);
        await Task.Delay(1500, Token);
        return (recorder, stop, watching);
    }

    [Fact]
    public async Task Mail_That_Arrives_Over_SMTP_Is_Reported_With_Its_Line_Of_The_List_And_The_New_Counters()
    {
        var account = await AccountAsync();
        await using var session = await Factory().OpenAsync(Credentials(account), Token);
        var (recorder, stop, watching) = await StartAsync(session);

        await DeliverAsync(account);
        await recorder.WaitForAsync(r => r.Of<MessageArrived>().Any() && r.Of<FolderCountersChanged>().Any());
        await stop.CancelAsync();
        await watching.WaitAsync(TimeSpan.FromSeconds(30), Token);

        var arrived = Assert.Single(recorder.Of<MessageArrived>());
        Assert.Equal("INBOX", arrived.Folder);
        Assert.Equal("Hello from the world", arrived.Message.Subject);
        Assert.Equal("anna@vinette.test", arrived.Message.From?.Address);
        Assert.False(arrived.Message.Seen);
        Assert.Equal("INBOX", arrived.Message.Id.Folder);
        var counters = recorder.Of<FolderCountersChanged>().Last();
        Assert.Equal((1, 1), (counters.Total, counters.Unread));
    }

    [Fact]
    public async Task A_Mark_Made_Elsewhere_Is_Reported_For_That_Message_With_The_Counters()
    {
        var account = await AccountAsync(messages: 2);
        await using var session = await Factory().OpenAsync(Credentials(account), Token);
        var (recorder, stop, watching) = await StartAsync(session);

        await dovecot.DoveadmAsync(Token, "flags", "add", "-u", account.Address, "\\Seen \\Flagged Work", "mailbox", "INBOX", "uid", "1");
        await recorder.WaitForAsync(r => r.Of<MessageMarksChanged>().Any() || r.Of<FolderStale>().Any());
        await recorder.WaitForAsync(r => r.Of<FolderCountersChanged>().Any());
        await stop.CancelAsync();
        await watching.WaitAsync(TimeSpan.FromSeconds(30), Token);

        var marks = Assert.Single(recorder.Of<MessageMarksChanged>());
        Assert.Equal(1u, marks.Id.Uid);
        Assert.True(marks.Seen);
        Assert.True(marks.Flagged);
        Assert.False(marks.Answered);
        Assert.Equal(["Work"], marks.Labels);
        var counters = recorder.Of<FolderCountersChanged>().Last();
        Assert.Equal((2, 1), (counters.Total, counters.Unread));
        Assert.Empty(recorder.Of<FolderStale>());
    }

    [Fact]
    public async Task A_Removal_Made_Elsewhere_Is_Reported_With_Its_Count_And_The_New_Counters()
    {
        var account = await AccountAsync(messages: 3);
        await using var session = await Factory().OpenAsync(Credentials(account), Token);
        var (recorder, stop, watching) = await StartAsync(session);

        await dovecot.DoveadmAsync(Token, "expunge", "-u", account.Address, "mailbox", "INBOX", "uid", "2");
        await recorder.WaitForAsync(r => r.Of<MessagesRemoved>().Any() && r.Of<FolderCountersChanged>().Any());
        await stop.CancelAsync();
        await watching.WaitAsync(TimeSpan.FromSeconds(30), Token);

        Assert.Equal(1, Assert.Single(recorder.Of<MessagesRemoved>()).Count);
        Assert.Equal(2, recorder.Of<FolderCountersChanged>().Last().Total);
        Assert.Empty(recorder.Of<MessageArrived>());
    }

    [Fact]
    public async Task A_Burst_Of_Mail_Is_Reported_One_By_One_Up_To_A_Limit_And_The_Folder_Is_Called_Stale_Beyond_It()
    {
        var account = await AccountAsync();
        await using var session = await Factory().OpenAsync(Credentials(account), Token);
        var (recorder, stop, watching) = await StartAsync(session);

        await AppendManyAsync(account, 30);
        await recorder.WaitForAsync(r => r.Of<FolderCountersChanged>().Any(c => c.Total == 30));
        await stop.CancelAsync();
        await watching.WaitAsync(TimeSpan.FromSeconds(30), Token);

        var arrived = recorder.Of<MessageArrived>().ToList();
        Assert.Equal(ImapMailSession.MaxArrivalsReported, arrived.Count);
        Assert.Equal(arrived.Select(a => a.Message.Id.Uid).Order(), arrived.Select(a => a.Message.Id.Uid)); // oldest of the reported first
        Assert.Contains(recorder.Events, e => e is FolderStale);
        Assert.Equal(30u, arrived.Max(a => a.Message.Id.Uid)); // the newest are the ones reported
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
        await recorder.WaitForAsync(r => r.Of<MessageArrived>().Any());
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
        await phone.WaitForAsync(r => r.Of<MessageArrived>().Any());
        await computer.WaitForAsync(r => r.Of<MessageArrived>().Any());

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
