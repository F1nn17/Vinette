using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vinette.Api.Configuration;
using Vinette.Api.Scheduling;
using Vinette.Core.Mail;
using Vinette.Core.Scheduling;
using Vinette.Mail;

namespace Vinette.Api.Tests;

/// <summary>
/// The queue of messages that are sent later (VIN-107, ADR-0008) with doubles for the mail server: what is refused, what goes when, how a message
/// survives a restart, that the password lives only as long as the message waits, and that a message is never lost.
/// </summary>
public sealed class ScheduledMailServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly MailCredentials Credentials = new("session-1", "me@vinette.test", "very-secret-pw");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly string directory = Path.Combine(Path.GetTempPath(), "vinette-scheduled-test-" + Guid.NewGuid().ToString("N"));
    private readonly Clock clock = new();
    private readonly FakeOutgoing outgoing = new();
    private readonly FakeGateway gateway = new();
    private readonly FakeSuggestions suggestions = new();
    private readonly IDataProtectionProvider protection = new EphemeralDataProtectionProvider();

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Utc { get; set; } = Now;

        public override DateTimeOffset GetUtcNow() => Utc;
    }

    private sealed class FakeOutgoing : IOutgoingMail
    {
        public List<(MailCredentials Credentials, ComposedMessage Message)> Sent { get; } = [];

        public Exception? Fail { get; set; }

        public Task<SendOutcome> SendAsync(MailCredentials credentials, OutgoingMessage message, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SendOutcome> SendComposedAsync(MailCredentials credentials, ComposedMessage message, CancellationToken cancellationToken)
        {
            if (Fail is not null)
            {
                throw Fail;
            }

            Sent.Add((credentials, message));
            return Task.FromResult(new SendOutcome(message, SentCopyOutcome.Saved, null));
        }
    }

    private sealed class FakeSuggestions : IRecipientSuggestions
    {
        public List<(string Mailbox, MailAddressInfo[] People)> Remembered { get; } = [];

        public Task<IReadOnlyList<MailAddressInfo>> SuggestAsync(MailCredentials credentials, string query, int count, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MailAddressInfo>>([]);

        public Task<IReadOnlyList<KnownRecipient>> LookupAsync(MailCredentials credentials, IReadOnlyList<string> addresses, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KnownRecipient>>([]);

        public void Remember(string mailbox, IEnumerable<MailAddressInfo> recipients) => Remembered.Add((mailbox, [.. recipients]));
    }

#pragma warning disable CA1852 // DispatchProxy needs a class that is not sealed.
    private class SessionDouble : DispatchProxy
    {
        public Dictionary<string, Func<object?[], object?>> Members { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Members.TryGetValue(targetMethod!.Name, out var member) ? member(args ?? []) : throw new NotSupportedException(targetMethod.Name);

        public static IMailSession Create(Dictionary<string, Func<object?[], object?>> members)
        {
            var proxy = DispatchProxy.Create<IMailSession, SessionDouble>();
            ((SessionDouble)(object)proxy).Members = members;
            return proxy;
        }
    }
#pragma warning restore CA1852

    private sealed class FakeGateway : IMailGateway
    {
        public List<(string Folder, string Mail, bool Draft)> Appended { get; } = [];

        public List<string> Released { get; } = [];

        public List<MailFolderInfo> Folders { get; } = [new("Drafts", "Drafts", null, FolderRole.Drafts, true, 0, 0, true)];

        public Exception? Fail { get; set; }

        public Task<TResult> ExecuteAsync<TResult>(MailCredentials credentials, Func<IMailSession, CancellationToken, Task<TResult>> command, CancellationToken cancellationToken, bool safeToRepeat = true)
        {
            if (Fail is not null)
            {
                throw Fail;
            }

            var session = SessionDouble.Create(new Dictionary<string, Func<object?[], object?>>
            {
                ["GetFoldersAsync"] = _ => Task.FromResult<IReadOnlyList<MailFolderInfo>>(Folders),
                ["AppendMessageAsync"] = args =>
                {
                    Appended.Add(((string)args[0]!, Encoding.UTF8.GetString(((ReadOnlyMemory<byte>)args[1]!).Span), (bool)args[3]!));
                    return Task.FromResult<MessageId?>(new MessageId((string)args[0]!, 1, (uint)Appended.Count));
                },
            });
            return command(session, cancellationToken);
        }

        public Task ReleaseAsync(string sessionId, CancellationToken cancellationToken)
        {
            Released.Add(sessionId);
            return Task.CompletedTask;
        }
    }

    private SchedulingOptions Options { get; } = new() { Path = null };

    private FileScheduledMailStore NewStore() => new(Microsoft.Extensions.Options.Options.Create(new SchedulingOptions { Path = directory }));

    private ScheduledMailService Service(FileScheduledMailStore? store = null, IDataProtectionProvider? provider = null, SchedulingOptions? options = null) =>
        new(
            store ?? NewStore(),
            new MimeMessageComposer(clock),
            outgoing,
            gateway,
            suggestions,
            provider ?? protection,
            Microsoft.Extensions.Options.Options.Create(options ?? Options),
            NullLogger<ScheduledMailService>.Instance,
            clock);

    private static OutgoingMessage Message(string subject = "Hello") => new(
        new MailAddressInfo("Me", "me@vinette.test"),
        [new MailAddressInfo("Anna", "anna@example.org")],
        [new MailAddressInfo("", "carol@example.org")],
        [new MailAddressInfo("", "hidden@example.org")],
        subject,
        "Hi there",
        null,
        []);

    private string AllFiles() => string.Join(
        "\n",
        Directory.Exists(directory) ? Directory.EnumerateFiles(directory).Select(File.ReadAllText) : []);

    [Fact]
    public async Task A_Message_Is_Queued_With_The_Mail_Built_Now_Dated_As_Scheduled_And_The_Hidden_Recipients_Kept_Out_Of_It()
    {
        var sendAt = Now.AddHours(3);

        var item = await Service().ScheduleAsync(Credentials, Message("Привет"), sendAt, Token);

        Assert.Equal((ScheduledState.Pending, sendAt, "Привет", "me@vinette.test"), (item.State, item.SendAt, item.Subject, item.Mailbox));
        var raw = Encoding.UTF8.GetString((await NewStore().ReadRawAsync(item.Id, Token))!);
        Assert.Contains("anna@example.org", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden@example.org", raw, StringComparison.Ordinal); // a hidden recipient is in the envelope only
        Assert.Contains("Date: Fri, 20 Nov 2026 15:00:00", raw, StringComparison.Ordinal);
        Assert.Equal(["hidden@example.org"], item.Bcc.Select(a => a.Address));
        Assert.Empty(outgoing.Sent); // nothing goes before its time
    }

    [Fact]
    public async Task The_Password_Is_Kept_Encrypted_Only_In_The_Description_Never_As_Text_And_Never_In_The_Mail()
    {
        _ = await Service().ScheduleAsync(Credentials, Message(), Now.AddHours(1), Token);

        var files = AllFiles();
        Assert.DoesNotContain(Credentials.Password, files, StringComparison.Ordinal);
        Assert.DoesNotContain(Credentials.SessionId, files, StringComparison.Ordinal);
        Assert.Contains("protectedPassword", files, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, ScheduleRefusal.TooSoon)]
    [InlineData(-3600, ScheduleRefusal.TooSoon)]
    [InlineData(30, ScheduleRefusal.TooSoon)]
    [InlineData(8 * 24 * 3600, ScheduleRefusal.TooLate)]
    public async Task A_Time_That_Is_Too_Soon_Or_Too_Far_Ahead_Is_Refused_And_Nothing_Is_Kept(int seconds, ScheduleRefusal expected)
    {
        var refused = await Assert.ThrowsAsync<ScheduleRefusedException>(() => Service().ScheduleAsync(Credentials, Message(), Now.AddSeconds(seconds), Token));

        Assert.Equal(expected, refused.Reason);
        Assert.Empty(await NewStore().ListAsync(Credentials.Address, Token));
    }

    [Fact]
    public async Task The_Times_At_The_Edges_Are_Accepted()
    {
        var service = Service();

        _ = await service.ScheduleAsync(Credentials, Message(), Now + Options.MinDelay, Token);
        _ = await service.ScheduleAsync(Credentials, Message(), Now + Options.MaxDelay, Token);

        Assert.Equal(2, (await NewStore().ListAsync(Credentials.Address, Token)).Count);
    }

    [Fact]
    public async Task A_Mailbox_Can_Have_Only_So_Many_Waiting_Messages_And_Failed_Notes_Do_Not_Count()
    {
        var options = new SchedulingOptions { MaxPerMailbox = 2 };
        var service = Service(options: options);
        var first = await service.ScheduleAsync(Credentials, Message(), Now.AddHours(1), Token);
        _ = await service.ScheduleAsync(Credentials, Message(), Now.AddHours(2), Token);

        var refused = await Assert.ThrowsAsync<ScheduleRefusedException>(() => service.ScheduleAsync(Credentials, Message(), Now.AddHours(3), Token));
        Assert.Equal(ScheduleRefusal.TooMany, refused.Reason);

        _ = await NewStore().UpdateAsync(first with { State = ScheduledState.Failed, ProtectedPassword = null }, Token);
        _ = await service.ScheduleAsync(Credentials, Message(), Now.AddHours(3), Token); // a note of a failure is no queue
    }

    [Fact]
    public async Task What_Cannot_Be_Built_Is_Refused_At_Once_Not_At_The_Time_Of_Sending()
    {
        var noRecipient = Message() with { To = [], Cc = [], Bcc = [] };

        _ = await Assert.ThrowsAsync<ArgumentException>(() => Service().ScheduleAsync(Credentials, noRecipient, Now.AddHours(1), Token));

        Assert.Empty(await NewStore().ListAsync(Credentials.Address, Token));
    }

    [Fact]
    public async Task Only_What_Is_Due_Goes_And_It_Goes_Once_With_The_Password_Gone_The_Recipients_Remembered_And_The_Connection_Let_Go()
    {
        var service = Service();
        var soon = await service.ScheduleAsync(Credentials, Message("Soon"), Now.AddMinutes(10), Token);
        _ = await service.ScheduleAsync(Credentials, Message("Later"), Now.AddHours(5), Token);

        clock.Utc = Now.AddMinutes(11);
        var dealt = await service.SendDueAsync(Token);
        var again = await service.SendDueAsync(Token);

        Assert.Equal((1, 0), (dealt, again));
        var (credentials, sent) = Assert.Single(outgoing.Sent);
        Assert.Equal(("me@vinette.test", Credentials.Password), (credentials.Address, credentials.Password)); // the password is given back to the mail, from the queue
        Assert.Equal(["anna@example.org", "carol@example.org", "hidden@example.org"], sent.Envelope.Recipients);
        Assert.Equal(soon.MessageId, sent.MessageId);
        Assert.Equal(["Later"], (await NewStore().ListAsync(Credentials.Address, Token)).Select(m => m.Subject));
        Assert.DoesNotContain("Soon", AllFiles(), StringComparison.Ordinal);
        Assert.Equal(["me@vinette.test"], suggestions.Remembered.Select(r => r.Mailbox));
        Assert.Equal(["scheduled-" + soon.Id], gateway.Released);
    }

    [Fact]
    public async Task A_Message_Survives_A_Restart_It_Goes_From_What_Was_Kept_On_Disk()
    {
        var keys = new DirectoryInfo(Path.Combine(directory, "keys"));
        var item = await Service(provider: DataProtectionProvider.Create(keys)).ScheduleAsync(Credentials, Message("Kept"), Now.AddMinutes(5), Token);

        clock.Utc = Now.AddMinutes(6);
        var afterRestart = Service(store: NewStore(), provider: DataProtectionProvider.Create(keys)); // a new process: new objects, the same disk and keys
        _ = await afterRestart.SendDueAsync(Token);

        var (credentials, sent) = Assert.Single(outgoing.Sent);
        Assert.Equal(Credentials.Password, credentials.Password);
        Assert.Equal(item.MessageId, sent.MessageId);
    }

    [Fact]
    public async Task While_The_Mail_Server_Cannot_Be_Reached_It_Is_Tried_Again_Later_And_Not_Sent_Twice()
    {
        var service = Service();
        var item = await service.ScheduleAsync(Credentials, Message(), Now.AddMinutes(5), Token);
        outgoing.Fail = new MailUnavailableException("down");

        clock.Utc = Now.AddMinutes(6);
        _ = await service.SendDueAsync(Token);
        var waiting = (await NewStore().GetAsync(Credentials.Address, item.Id, Token))!;
        _ = await service.SendDueAsync(Token); // not yet: the pause between tries has not passed

        Assert.Equal((ScheduledState.Pending, 1), (waiting.State, waiting.Attempts));
        Assert.Equal(clock.Utc + Options.RetryEvery, waiting.NextAttempt);
        Assert.NotNull(waiting.ProtectedPassword);
        Assert.Empty(gateway.Appended);

        outgoing.Fail = null;
        clock.Utc += Options.RetryEvery + TimeSpan.FromSeconds(1);
        _ = await service.SendDueAsync(Token);
        _ = Assert.Single(outgoing.Sent);
        Assert.Empty(await NewStore().ListAsync(Credentials.Address, Token));
    }

    [Fact]
    public async Task When_The_Time_For_Tries_Is_Up_The_Message_Goes_To_The_Drafts_With_Its_Hidden_Recipients_And_Only_A_Note_Remains_Without_The_Password()
    {
        var service = Service();
        var item = await service.ScheduleAsync(Credentials, Message("Not sent"), Now.AddMinutes(5), Token);
        outgoing.Fail = new MailUnavailableException("down");

        clock.Utc = Now.AddMinutes(5) + Options.RetryFor + TimeSpan.FromMinutes(1);
        _ = await service.SendDueAsync(Token);

        var (folder, mail, draft) = Assert.Single(gateway.Appended);
        Assert.True(draft);
        Assert.Equal("Drafts", folder);
        Assert.Contains("Bcc: hidden@example.org", mail, StringComparison.Ordinal); // the draft keeps the hidden recipient the mail does not have
        Assert.Contains("Not sent", mail, StringComparison.Ordinal);
        var note = (await NewStore().GetAsync(Credentials.Address, item.Id, Token))!;
        Assert.Equal((ScheduledState.Failed, "mail_unavailable", true, null), (note.State, note.Problem, note.InDrafts, note.ProtectedPassword));
        Assert.Null(await NewStore().ReadRawAsync(item.Id, Token)); // the mail is among the drafts now: no second copy here
        Assert.DoesNotContain(Credentials.Password, AllFiles(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SendRejection.Recipient, "recipient_rejected")]
    [InlineData(SendRejection.TooLarge, "send_too_large")]
    [InlineData(SendRejection.Content, "send_refused")]
    [InlineData(SendRejection.Sender, "send_refused")]
    public async Task A_Refusal_Of_The_Mail_Server_Puts_The_Message_In_The_Drafts_At_Once_And_Is_Not_Tried_Again(SendRejection reason, string code)
    {
        var service = Service();
        var item = await service.ScheduleAsync(Credentials, Message(), Now.AddMinutes(5), Token);
        outgoing.Fail = new MailRejectedException(reason, "no");

        clock.Utc = Now.AddMinutes(6);
        _ = await service.SendDueAsync(Token);
        _ = await service.SendDueAsync(Token);

        _ = Assert.Single(gateway.Appended);
        var note = (await NewStore().GetAsync(Credentials.Address, item.Id, Token))!;
        Assert.Equal((ScheduledState.Failed, code, true), (note.State, note.Problem, note.InDrafts));
        Assert.Empty(outgoing.Sent);
    }

    [Fact]
    public async Task A_Connection_That_Broke_After_The_Mail_Was_Handed_Over_Is_Never_Tried_Again_The_Message_May_Have_Gone()
    {
        var service = Service();
        var item = await service.ScheduleAsync(Credentials, Message(), Now.AddMinutes(5), Token);
        outgoing.Fail = new MailUnavailableException("broke", new IOException("x"), messageMayHaveBeenSent: true);

        clock.Utc = Now.AddMinutes(6);
        _ = await service.SendDueAsync(Token);
        outgoing.Fail = null;
        clock.Utc = Now.AddHours(1);
        _ = await service.SendDueAsync(Token);

        Assert.Empty(outgoing.Sent);
        var note = (await NewStore().GetAsync(Credentials.Address, item.Id, Token))!;
        Assert.Equal((ScheduledState.Failed, "send_uncertain", true), (note.State, note.Problem, note.InDrafts));
        _ = Assert.Single(gateway.Appended);
    }

    [Fact]
    public async Task When_The_Mail_Takes_The_Password_No_More_The_Message_Waits_Without_It_And_The_Next_Sign_In_Puts_It_In_The_Drafts()
    {
        var service = Service();
        var item = await service.ScheduleAsync(Credentials, Message("Wait"), Now.AddMinutes(5), Token);
        outgoing.Fail = new MailAccessRevokedException("changed");
        gateway.Fail = new MailAccessRevokedException("changed"); // the drafts cannot be reached either

        clock.Utc = Now.AddMinutes(6);
        _ = await service.SendDueAsync(Token);

        var note = (await NewStore().GetAsync(Credentials.Address, item.Id, Token))!;
        Assert.Equal((ScheduledState.Failed, "session_revoked", false, null), (note.State, note.Problem, note.InDrafts, note.ProtectedPassword));
        Assert.NotNull(await NewStore().ReadRawAsync(item.Id, Token)); // nothing is lost
        Assert.DoesNotContain(Credentials.Password, AllFiles(), StringComparison.Ordinal);

        gateway.Fail = null; // the user signs in again, with the new password, and looks at the list
        var listed = await service.ListAsync(Credentials with { Password = "new-password" }, Token);

        var rescued = Assert.Single(listed);
        Assert.True(rescued.InDrafts);
        Assert.Contains("Bcc: hidden@example.org", Assert.Single(gateway.Appended).Mail, StringComparison.Ordinal);
        Assert.Null(await NewStore().ReadRawAsync(item.Id, Token));
    }

    [Fact]
    public async Task A_Password_That_Cannot_Be_Read_Any_More_Fails_The_Message_Without_Sending_Anything()
    {
        var item = await Service().ScheduleAsync(Credentials, Message(), Now.AddMinutes(5), Token);
        var lostKeys = Service(provider: new EphemeralDataProtectionProvider()); // another key ring: the keys of the queue are gone

        clock.Utc = Now.AddMinutes(6);
        _ = await lostKeys.SendDueAsync(Token);

        Assert.Empty(outgoing.Sent);
        var note = (await NewStore().GetAsync(Credentials.Address, item.Id, Token))!;
        Assert.Equal((ScheduledState.Failed, "session_revoked", null), (note.State, note.Problem, note.ProtectedPassword));
    }

    [Fact]
    public async Task Cancelling_Puts_The_Message_Back_Among_The_Drafts_With_Its_Hidden_Recipients_And_Leaves_The_Queue()
    {
        var service = Service();
        var item = await service.ScheduleAsync(Credentials, Message("Changed my mind"), Now.AddHours(2), Token);

        var outcome = await service.CancelAsync(Credentials, item.Id, Token);

        Assert.Equal(CancelOutcome.Cancelled, outcome);
        var (folder, mail, draft) = Assert.Single(gateway.Appended);
        Assert.True(draft);
        Assert.Equal("Drafts", folder);
        Assert.Contains("Changed my mind", mail, StringComparison.Ordinal);
        Assert.Contains("Bcc: hidden@example.org", mail, StringComparison.Ordinal);
        Assert.Empty(await NewStore().ListAsync(Credentials.Address, Token));
        Assert.DoesNotContain(Credentials.Password, AllFiles(), StringComparison.Ordinal);
        clock.Utc = Now.AddHours(3);
        Assert.Equal(0, await service.SendDueAsync(Token)); // and it never goes
    }

    [Fact]
    public async Task Without_A_Drafts_Folder_Cancelling_Refuses_And_The_Message_Stays_Queued()
    {
        var service = Service();
        var item = await service.ScheduleAsync(Credentials, Message(), Now.AddHours(2), Token);
        gateway.Folders.Clear();

        var outcome = await service.CancelAsync(Credentials, item.Id, Token);

        Assert.Equal(CancelOutcome.NoDraftsFolder, outcome);
        _ = Assert.Single(await NewStore().ListAsync(Credentials.Address, Token));
    }

    [Fact]
    public async Task Nobody_Can_Cancel_Or_See_A_Message_Of_Another_Mailbox_Or_One_That_Does_Not_Exist()
    {
        var service = Service();
        var item = await service.ScheduleAsync(Credentials, Message(), Now.AddHours(2), Token);
        var other = Credentials with { Address = "bob@vinette.test" };

        Assert.Equal(CancelOutcome.NotFound, await service.CancelAsync(other, item.Id, Token));
        Assert.Equal(CancelOutcome.NotFound, await service.CancelAsync(Credentials, "../../etc/passwd", Token));
        Assert.Equal(CancelOutcome.NotFound, await service.CancelAsync(Credentials, new string('0', 32), Token));
        Assert.Empty(await service.ListAsync(other, Token));
        _ = Assert.Single(await service.ListAsync(Credentials, Token));
    }

    [Fact]
    public async Task The_Note_Of_A_Failure_Is_Removed_By_Cancelling_Without_Putting_Anything_In_The_Drafts_Again()
    {
        var service = Service();
        var item = await service.ScheduleAsync(Credentials, Message(), Now.AddMinutes(5), Token);
        outgoing.Fail = new MailRejectedException(SendRejection.Content, "no");
        clock.Utc = Now.AddMinutes(6);
        _ = await service.SendDueAsync(Token);
        Assert.Single(gateway.Appended);

        var outcome = await service.CancelAsync(Credentials, item.Id, Token);

        Assert.Equal(CancelOutcome.Cancelled, outcome);
        _ = Assert.Single(gateway.Appended); // still the one
        Assert.Empty(await NewStore().ListAsync(Credentials.Address, Token));
    }

    [Fact]
    public async Task A_Message_Cancelled_While_It_Is_Being_Sent_Does_Not_Come_Back_As_A_Failure()
    {
        var store = NewStore();
        var service = Service(store);
        var item = await service.ScheduleAsync(Credentials, Message(), Now.AddMinutes(5), Token);
        outgoing.Fail = new MailRejectedException(SendRejection.Content, "no");
        _ = await store.RemoveAsync(Credentials.Address, item.Id, Token);

        clock.Utc = Now.AddMinutes(6);
        _ = await service.SendDueAsync(Token);

        Assert.Empty(await store.ListAsync(Credentials.Address, Token));
        Assert.Empty(gateway.Appended);
    }
}
