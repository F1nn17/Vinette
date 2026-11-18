using System.Reflection;
using System.Text;
using Vinette.Api.Mail;
using Vinette.Core.Mail;
using Vinette.Mail;

namespace Vinette.Api.Tests;

/// <summary>
/// Drafts (VIN-094) with a double for the mail server: what is appended and how it is flagged, that the version it replaces is removed
/// only after the new one is safe, that only the drafts folder can be touched, and that a saved draft opens as what was written
/// (with its files and hidden recipients). No mail server needed.
/// </summary>
public sealed class DraftServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly MailCredentials Credentials = new("s", "me@vinette.test", "pw");
    private static readonly MessageId NewId = new("Drafts", 7, 50);
    private static readonly MessageId OldId = new("Drafts", 7, 49);

    private static MailFolderInfo Folder(string path, FolderRole role, bool canContain = true) =>
        new(path, path, null, role, canContain, 0, 0, true);

    private static readonly IReadOnlyList<MailFolderInfo> Folders = [Folder("INBOX", FolderRole.Inbox), Folder("Drafts", FolderRole.Drafts), Folder("Sent", FolderRole.Sent)];

    private static OutgoingMessage Message(IReadOnlyList<MailAddressInfo>? to = null, IReadOnlyList<OutgoingAttachment>? files = null) => new(
        new MailAddressInfo("Me", "me@vinette.test"),
        to ?? [new MailAddressInfo("Anna", "anna@example.org")],
        [new MailAddressInfo("", "carol@example.org")],
        [new MailAddressInfo("", "hidden@example.org")],
        "Привет",
        "Hi there",
        null,
        files ?? [],
        InReplyTo: "<orig@example.org>",
        References: ["first@example.org", "orig@example.org"]);

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

        public Task<TResult> ExecuteAsync<TResult>(MailCredentials credentials, Func<IMailSession, CancellationToken, Task<TResult>> command, CancellationToken cancellationToken, bool safeToRepeat = true)
        {
            SafeToRepeat.Add(safeToRepeat);
            return command(session, cancellationToken);
        }

        public Task ReleaseAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Server
    {
        public List<string> Order { get; } = [];

        public List<(string Folder, byte[] Message, bool Seen, bool Draft)> Appended { get; } = [];

        public List<MessageId> Deleted { get; } = [];

        public Func<MessageId, Task>? Delete { get; set; }

        public byte[]? Stored { get; set; }

        public IReadOnlyList<MailFolderInfo> FolderList { get; set; } = Folders;
    }

    private static (DraftService Service, Server Server, Gateway Gateway) Setup(Server? server = null)
    {
        server ??= new Server();
        var state = server;
        var session = SessionDouble.Create(new Dictionary<string, Func<object?[], object?>>
        {
            ["GetFoldersAsync"] = _ => Task.FromResult(state.FolderList),
            ["AppendMessageAsync"] = args =>
            {
                state.Order.Add("append");
                var raw = ((ReadOnlyMemory<byte>)args[1]!).ToArray();
                state.Stored = raw;
                state.Appended.Add(((string)args[0]!, raw, (bool)args[2]!, (bool)args[3]!));
                return Task.FromResult<MessageId?>(NewId);
            },
            ["DeleteMessagePermanentlyAsync"] = args =>
            {
                state.Order.Add("delete");
                var id = (MessageId)args[0]!;
                state.Deleted.Add(id);
                return state.Delete is null ? Task.CompletedTask : state.Delete(id);
            },
            ["GetMessageAsync"] = args =>
            {
                var id = (MessageId)args[0]!;
                return Task.FromResult(new MessageSource(id, state.Stored ?? [], true, false, false, true, []));
            },
        });
        var gateway = new Gateway(session);
        return (new DraftService(new MimeMessageComposer(), new MimeMessageParser(), gateway), server, gateway);
    }

    [Fact]
    public async Task A_Draft_Is_Appended_To_The_Drafts_Folder_Flagged_As_A_Draft_Once()
    {
        var (service, server, gateway) = Setup();

        var saved = await service.SaveAsync(Credentials, Message(), null, Token);

        Assert.NotNull(saved);
        Assert.Equal(NewId, saved.Id);
        Assert.True(saved.PreviousRemoved);
        var append = Assert.Single(server.Appended);
        Assert.Equal("Drafts", append.Folder);
        Assert.True(append.Draft);
        Assert.True(append.Seen);
        Assert.Empty(server.Deleted);
        Assert.Equal([false], gateway.SafeToRepeat); // an append must never be repeated by the gateway
    }

    [Fact]
    public async Task A_Draft_Needs_No_Recipient()
    {
        var (service, server, _) = Setup();

        var saved = await service.SaveAsync(Credentials, Message(to: []) with { Cc = [], Bcc = [] }, null, Token);

        Assert.NotNull(saved);
        Assert.Single(server.Appended);
    }

    [Fact]
    public async Task The_Hidden_Recipients_Are_Kept_In_The_Draft_But_Not_In_A_Mail_That_Is_Sent()
    {
        var composer = new MimeMessageComposer();

        var draft = Encoding.ASCII.GetString(composer.ComposeDraft(Message()).Raw.Span);
        var sent = Encoding.ASCII.GetString(composer.Compose(Message()).Raw.Span);

        Assert.Contains("Bcc: hidden@example.org", draft, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden@example.org", sent, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Message_That_Is_Sent_Still_Needs_A_Recipient()
    {
        var composer = new MimeMessageComposer();

        Assert.Throws<ArgumentException>(() => composer.Compose(Message(to: []) with { Cc = [], Bcc = [] }));
        Assert.Throws<ArgumentException>(() => composer.ComposeDraft(Message(to: [new MailAddressInfo("", "not an address")])));
    }

    [Fact]
    public async Task The_Version_It_Replaces_Is_Removed_After_The_New_One_Is_Kept()
    {
        var (service, server, gateway) = Setup();

        var saved = await service.SaveAsync(Credentials, Message(), OldId, Token);

        Assert.Equal(["append", "delete"], server.Order);
        Assert.Equal([OldId], server.Deleted);
        Assert.True(saved!.PreviousRemoved);
        Assert.Equal([false], gateway.SafeToRepeat);
    }

    [Fact]
    public async Task A_Version_That_Is_Already_Gone_Is_Not_An_Error()
    {
        var server = new Server { Delete = _ => throw new MailMessageNotFoundException("gone") };
        var (service, _, _) = Setup(server);

        var saved = await service.SaveAsync(Credentials, Message(), OldId, Token);

        Assert.Equal(NewId, saved!.Id);
        Assert.True(saved.PreviousRemoved);
    }

    [Fact]
    public async Task When_The_Old_Version_Cannot_Be_Removed_The_New_One_Is_Still_Reported()
    {
        var server = new Server { Delete = _ => throw new MailUnavailableException("down") };
        var (service, _, _) = Setup(server);

        var saved = await service.SaveAsync(Credentials, Message(), OldId, Token);

        Assert.Equal(NewId, saved!.Id);
        Assert.False(saved.PreviousRemoved);
    }

    [Fact]
    public async Task A_Version_Outside_The_Drafts_Folder_Is_Refused_And_Nothing_Is_Saved()
    {
        var (service, server, _) = Setup();

        await Assert.ThrowsAsync<MailMessageNotFoundException>(() => service.SaveAsync(Credentials, Message(), new MessageId("INBOX", 1, 5), Token));

        Assert.Empty(server.Appended);
        Assert.Empty(server.Deleted);
    }

    [Fact]
    public async Task Without_A_Drafts_Folder_Nothing_Is_Saved_And_No_Folder_Is_Made()
    {
        var server = new Server { FolderList = [Folder("INBOX", FolderRole.Inbox), Folder("Sent", FolderRole.Sent)] };
        var (service, _, _) = Setup(server);

        Assert.Null(await service.SaveAsync(Credentials, Message(), null, Token));
        Assert.Null(await service.OpenAsync(Credentials, OldId, Token));
        Assert.False(await service.DeleteAsync(Credentials, OldId, Token));
        Assert.Empty(server.Appended);
        Assert.Empty(server.Deleted);
    }

    [Fact]
    public async Task A_Saved_Draft_Opens_As_What_Was_Written_With_Its_Files()
    {
        var (service, _, _) = Setup();
        var file = new OutgoingAttachment("отчёт.pdf", "application/pdf", new byte[] { 1, 2, 3, 4, 5 });
        var saved = await service.SaveAsync(Credentials, Message(files: [file]), null, Token);

        var opened = await service.OpenAsync(Credentials, saved!.Id!.Value, Token);

        Assert.NotNull(opened);
        var message = opened.Message;
        Assert.Equal("me@vinette.test", message.From.Address);
        Assert.Equal(["anna@example.org"], message.To.Select(a => a.Address));
        Assert.Equal(["carol@example.org"], message.Cc.Select(a => a.Address));
        Assert.Equal(["hidden@example.org"], message.Bcc.Select(a => a.Address));
        Assert.Equal("Привет", message.Subject);
        Assert.Equal("Hi there", message.Text?.Trim());
        Assert.Equal(saved.MessageId, message.MessageId);
        Assert.Equal("orig@example.org", message.InReplyTo);
        Assert.Equal(["first@example.org", "orig@example.org"], message.References);
        var back = Assert.Single(message.Attachments);
        Assert.Equal("отчёт.pdf", back.FileName);
        Assert.Equal("application/pdf", back.ContentType);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, back.Content.ToArray());
    }

    [Fact]
    public async Task A_Draft_Saved_Again_Keeps_Its_Message_Id()
    {
        var (service, _, _) = Setup();
        var first = await service.SaveAsync(Credentials, Message(), null, Token);

        var second = await service.SaveAsync(Credentials, Message() with { MessageId = first!.MessageId }, NewId, Token);

        Assert.Equal(first.MessageId, second!.MessageId);
    }

    [Fact]
    public async Task Only_Messages_Of_The_Drafts_Folder_Can_Be_Opened_Or_Deleted()
    {
        var (service, server, _) = Setup();
        var elsewhere = new MessageId("INBOX", 1, 5);

        await Assert.ThrowsAsync<MailMessageNotFoundException>(() => service.OpenAsync(Credentials, elsewhere, Token));
        await Assert.ThrowsAsync<MailMessageNotFoundException>(() => service.DeleteAsync(Credentials, elsewhere, Token));

        Assert.Empty(server.Deleted);
    }

    [Fact]
    public async Task A_Draft_Is_Deleted_For_Good_Once()
    {
        var (service, server, gateway) = Setup();

        Assert.True(await service.DeleteAsync(Credentials, OldId, Token));

        Assert.Equal([OldId], server.Deleted);
        Assert.Equal([false], gateway.SafeToRepeat);
    }
}
