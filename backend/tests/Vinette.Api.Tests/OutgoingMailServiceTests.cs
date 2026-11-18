using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Vinette.Api.Mail;
using Vinette.Core.Mail;
using Vinette.Mail;

namespace Vinette.Api.Tests;

/// <summary>
/// Sending and keeping a copy (VIN-092) with doubles for the mail server: the order of things, what happens to a copy that cannot be
/// kept, and that the message is never sent twice. No mail server needed.
/// </summary>
public sealed class OutgoingMailServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly MailCredentials Credentials = new("s", "me@vinette.test", "pw");
    private static readonly MessageId CopyId = new("Sent", 7, 42);

    private static OutgoingMessage Message() => new(
        new MailAddressInfo("Me", "me@vinette.test"),
        [new MailAddressInfo("Anna", "anna@example.org")],
        [],
        [new MailAddressInfo("", "hidden@example.org")],
        "Hello",
        "Hi",
        null,
        []);

    private static MailFolderInfo Folder(string path, FolderRole role, bool canContain = true) =>
        new(path, path, null, role, canContain, 0, 0, true);

    /// <summary>A session whose members are given by name; every other member throws.</summary>
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

    private sealed class Gateway(IMailSession session) : IMailGateway
    {
        public List<bool> SafeToRepeat { get; } = [];

        public List<CancellationToken> Tokens { get; } = [];

        public Task<TResult> ExecuteAsync<TResult>(MailCredentials credentials, Func<IMailSession, CancellationToken, Task<TResult>> command, CancellationToken cancellationToken, bool safeToRepeat = true)
        {
            SafeToRepeat.Add(safeToRepeat);
            Tokens.Add(cancellationToken);
            return command(session, cancellationToken);
        }

        public Task ReleaseAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Sender : IMailSender
    {
        public List<(OutgoingEnvelope Envelope, byte[] Message)> Sent { get; } = [];

        public Func<Task>? Before { get; set; }

        public Exception? Fail { get; set; }

        public async Task SendAsync(MailCredentials credentials, OutgoingEnvelope envelope, ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
        {
            if (Before is not null)
            {
                await Before();
            }

            if (Fail is not null)
            {
                throw Fail;
            }

            Sent.Add((envelope, message.ToArray()));
        }
    }

    private sealed class Appended
    {
        public List<(string Folder, byte[] Message, bool Seen, DateTimeOffset At)> Calls { get; } = [];
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static (OutgoingMailService Service, Sender Sender, Gateway Gateway, Appended Appended, List<string> Order) Setup(
        IReadOnlyList<MailFolderInfo>? folders = null,
        Func<Task<MessageId?>>? append = null,
        DateTimeOffset? now = null)
    {
        var order = new List<string>();
        var appended = new Appended();
        var sender = new Sender { Before = () => { order.Add("send"); return Task.CompletedTask; } };
        var session = SessionDouble.Create(new Dictionary<string, Func<object?[], object?>>
        {
            ["GetFoldersAsync"] = _ => Task.FromResult(folders ?? [Folder("INBOX", FolderRole.Inbox), Folder("Sent", FolderRole.Sent)]),
            ["AppendMessageAsync"] = args =>
            {
                order.Add("append");
                appended.Calls.Add(((string)args[0]!, ((ReadOnlyMemory<byte>)args[1]!).ToArray(), (bool)args[2]!, (DateTimeOffset)args[3]!));
                return append is null ? Task.FromResult<MessageId?>(CopyId) : append();
            },
        });
        var gateway = new Gateway(session);
        var service = new OutgoingMailService(new MimeMessageComposer(), sender, gateway, NullLogger<OutgoingMailService>.Instance, now is null ? null : new FixedTime(now.Value));
        return (service, sender, gateway, appended, order);
    }

    [Fact]
    public async Task The_Message_Is_Sent_First_And_Then_A_Copy_Of_Exactly_That_Mail_Is_Kept_As_Read()
    {
        var sentAt = new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);
        var (service, sender, _, appended, order) = Setup(now: sentAt);

        var outcome = await service.SendAsync(Credentials, Message(), Token);

        Assert.Equal(["send", "append"], order);
        Assert.Equal(SentCopyOutcome.Saved, outcome.Copy);
        Assert.Equal(CopyId, outcome.CopyId);
        var call = Assert.Single(appended.Calls);
        Assert.Equal("Sent", call.Folder);
        Assert.True(call.Seen);
        Assert.Equal(sentAt, call.At);
        Assert.Equal(sender.Sent[0].Message, call.Message); // the copy is the very mail that went out
        Assert.Equal(outcome.Message.Raw.ToArray(), call.Message);
        Assert.Equal(["anna@example.org", "hidden@example.org"], sender.Sent[0].Envelope.Recipients); // hidden recipient: sent to, not in the copy's headers
        Assert.DoesNotContain("hidden@example.org", System.Text.Encoding.ASCII.GetString(call.Message), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_Copy_Is_Kept_Once_And_Is_Never_Repeated_By_The_Gateway()
    {
        var (service, _, gateway, appended, _) = Setup();

        _ = await service.SendAsync(Credentials, Message(), Token);

        Assert.Single(appended.Calls);
        Assert.Equal([false], gateway.SafeToRepeat); // a mail appended twice would be there twice
    }

    [Fact]
    public async Task A_Message_The_Server_Refused_Is_Not_Copied_And_The_Refusal_Reaches_The_Caller()
    {
        var (service, sender, gateway, appended, _) = Setup();
        sender.Fail = new MailRejectedException(SendRejection.Recipient, "refused");

        var failure = await Record.ExceptionAsync(() => service.SendAsync(Credentials, Message(), Token));

        Assert.IsType<MailRejectedException>(failure);
        Assert.Empty(appended.Calls);
        Assert.Empty(gateway.SafeToRepeat); // the mailbox was not even asked
    }

    [Fact]
    public async Task A_Message_That_May_Have_Been_Sent_Is_Not_Copied_Either_And_The_Caller_Is_Told()
    {
        var (service, sender, _, appended, _) = Setup();
        sender.Fail = new MailUnavailableException("broken", new IOException("x"), messageMayHaveBeenSent: true);

        var failure = await Record.ExceptionAsync(() => service.SendAsync(Credentials, Message(), Token));

        Assert.True(Assert.IsType<MailUnavailableException>(failure).MessageMayHaveBeenSent);
        Assert.Empty(appended.Calls); // whether to keep a copy of what may not have gone out is for the user to decide
    }

    [Fact]
    public async Task A_Message_That_Cannot_Be_Built_Is_Neither_Sent_Nor_Copied()
    {
        var (service, sender, _, appended, _) = Setup();
        var nobody = Message() with { To = [], Bcc = [] };

        var failure = await Record.ExceptionAsync(() => service.SendAsync(Credentials, nobody, Token));

        Assert.IsType<ArgumentException>(failure);
        Assert.Empty(sender.Sent);
        Assert.Empty(appended.Calls);
    }

    [Fact]
    public async Task A_Mailbox_Without_A_Sent_Folder_Gets_No_Copy_And_No_New_Folder_And_The_Message_Still_Goes()
    {
        var (service, sender, _, appended, _) = Setup(folders: [Folder("INBOX", FolderRole.Inbox), Folder("Trash", FolderRole.Trash)]);

        var outcome = await service.SendAsync(Credentials, Message(), Token);

        Assert.Equal(SentCopyOutcome.NoSentFolder, outcome.Copy);
        Assert.Null(outcome.CopyId);
        Assert.Single(sender.Sent);
        Assert.Empty(appended.Calls);
    }

    [Fact]
    public async Task A_Folder_That_Only_Groups_Others_Is_Not_A_Place_For_The_Copy()
    {
        var (service, _, _, appended, _) = Setup(folders: [Folder("INBOX", FolderRole.Inbox), Folder("Sent", FolderRole.Sent, canContain: false)]);

        var outcome = await service.SendAsync(Credentials, Message(), Token);

        Assert.Equal(SentCopyOutcome.NoSentFolder, outcome.Copy);
        Assert.Empty(appended.Calls);
    }

    [Fact]
    public async Task The_Sent_Folder_Is_Found_By_Name_When_The_Server_Does_Not_Mark_It()
    {
        // the same roles as the sidebar shows (FolderRoleGuesser): "Отправленные" at the top level is the Sent folder
        var (service, _, _, appended, _) = Setup(folders: [Folder("INBOX", FolderRole.Inbox), Folder("Отправленные", FolderRole.Other)]);

        var outcome = await service.SendAsync(Credentials, Message(), Token);

        Assert.Equal(SentCopyOutcome.Saved, outcome.Copy);
        Assert.Equal("Отправленные", Assert.Single(appended.Calls).Folder);
    }

    [Theory]
    [MemberData(nameof(CopyFailures))]
    public async Task A_Copy_That_Cannot_Be_Kept_Is_Reported_And_Never_Fails_The_Send(Exception failure)
    {
        var (service, sender, _, appended, _) = Setup(append: () => throw failure);

        var outcome = await service.SendAsync(Credentials, Message(), Token);

        Assert.Equal(SentCopyOutcome.Failed, outcome.Copy);
        Assert.Null(outcome.CopyId);
        Assert.Single(sender.Sent); // it went out, once
        Assert.Single(appended.Calls); // and the copy was tried once
    }

    public static TheoryData<Exception> CopyFailures() => new()
    {
        new MailUnavailableException("down", new IOException("x")),
        new MailBusyException("busy"),
        new MailAccessRevokedException("revoked"),
        new MailFolderNotFoundException("gone"),
        new OperationCanceledException(),
    };

    [Fact]
    public async Task A_Request_That_Is_Cancelled_Once_The_Message_Is_Out_Does_Not_Cost_The_Copy()
    {
        using var request = new CancellationTokenSource();
        var (service, sender, gateway, appended, _) = Setup();
        var originalBefore = sender.Before;
        sender.Before = async () =>
        {
            await originalBefore!();
            await request.CancelAsync(); // the client goes away while the message is being handed in
        };

        var outcome = await service.SendAsync(Credentials, Message(), request.Token);

        Assert.Equal(SentCopyOutcome.Saved, outcome.Copy);
        Assert.Single(appended.Calls);
        Assert.False(gateway.Tokens[0].IsCancellationRequested);
        Assert.NotEqual(request.Token, gateway.Tokens[0]); // the copy runs on a token of its own
    }

    [Fact]
    public async Task A_Server_That_Does_Not_Say_Where_The_Copy_Went_Still_Counts_As_Saved()
    {
        var (service, _, _, _, _) = Setup(append: () => Task.FromResult<MessageId?>(null));

        var outcome = await service.SendAsync(Credentials, Message(), Token);

        Assert.Equal(SentCopyOutcome.Saved, outcome.Copy);
        Assert.Null(outcome.CopyId);
    }
}
