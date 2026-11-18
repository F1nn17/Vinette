using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Vinette.Api.Mail;
using Vinette.Core.Mail;

namespace Vinette.Api.Tests;

/// <summary>Suggestions of recipients (VIN-095) with a double for the mail server: what is read and how much, how matches are ranked, and that an answer comes from memory.</summary>
public sealed class RecipientSuggestionServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly MailCredentials Credentials = new("s", "me@vinette.test", "pw");

    private static MailFolderInfo Folder(string path, FolderRole role) => new(path, path, null, role, true, 0, 0, true);

    private sealed record Mail(DateTimeOffset Date, MailAddressInfo? From, params MailAddressInfo[] To);

    private static MailAddressInfo Person(string name, string address) => new(name, address);

    /// <summary>A mail server whose folders hold the given mail (newest first by position in the list).</summary>
    private sealed class Server
    {
        public Dictionary<string, List<Mail>> Folders { get; } = new() { ["INBOX"] = [], ["Sent"] = [] };

        public List<MailFolderInfo> FolderList { get; set; } = [Folder("INBOX", FolderRole.Inbox), Folder("Sent", FolderRole.Sent)];

        public int Commands;

        public int Served;

        public Exception? Fail { get; set; }

        public TaskCompletionSource? Gate { get; set; }

        public IMailSession Session() => SessionDouble.Create(new Dictionary<string, Func<object?[], object?>>
        {
            ["GetFoldersAsync"] = _ => Task.FromResult<IReadOnlyList<MailFolderInfo>>(FolderList),
            ["ListMessagesAsync"] = args => ListAsync((MessageListQuery)args[0]!),
        });

        private Task<MessagePage> ListAsync(MessageListQuery query)
        {
            var all = Folders[query.Folder];
            var newest = all.Count - (query.After is { } after ? (int)after.Uid : 0); // the cursor is how many were served
            var take = Math.Min(query.Limit, newest);
            var items = new List<MessageListItem>(take);
            for (var i = 0; i < take; i++)
            {
                var index = newest - 1 - i;
                var mail = all[all.Count - 1 - ((all.Count - newest) + i)];
                items.Add(new MessageListItem(new MessageId(query.Folder, 1, (uint)(index + 1)), mail.From, mail.To, string.Empty, mail.Date, true, false, false, false, false, [], 0, string.Empty));
            }

            _ = Interlocked.Add(ref Served, take);
            var served = (query.After is { } a ? (int)a.Uid : 0) + take;
            var next = served < all.Count && take > 0 ? new MessageCursor(1, (uint)served) : (MessageCursor?)null;
            return Task.FromResult(new MessagePage(1, items, next, all.Count));
        }
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

    private sealed class Gateway(Server server) : IMailGateway
    {
        public async Task<TResult> ExecuteAsync<TResult>(MailCredentials credentials, Func<IMailSession, CancellationToken, Task<TResult>> command, CancellationToken cancellationToken, bool safeToRepeat = true)
        {
            _ = Interlocked.Increment(ref server.Commands);
            if (server.Gate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            return server.Fail is not null ? throw server.Fail : await command(server.Session(), cancellationToken);
        }

        public Task ReleaseAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Utc { get; set; } = Now;

        public override DateTimeOffset GetUtcNow() => Utc;
    }

    private static RecipientSuggestionService Service(Server server, Clock? clock = null) =>
        new(new Gateway(server), NullLogger<RecipientSuggestionService>.Instance, clock ?? new Clock());

    private static async Task<string[]> AddressesAsync(RecipientSuggestionService service, string query, int count = 8) =>
        [.. (await service.SuggestAsync(Credentials, query, count, Token)).Select(a => a.Address)];

    private static Server Typical()
    {
        var server = new Server();
        // Oldest first in the list: the list is read from its end.
        server.Folders["Sent"].AddRange(
        [
            new Mail(Now.AddDays(-40), Person("Me", "me@vinette.test"), Person("Anna Петрова", "anna@example.org")),
            new Mail(Now.AddDays(-30), Person("Me", "me@vinette.test"), Person("Борис", "boris@example.org"), Person("", "me@vinette.test")),
            new Mail(Now.AddDays(-2), Person("Me", "me@vinette.test"), Person("Анна Петрова", "anna@example.org")),
        ]);
        server.Folders["INBOX"].AddRange(
        [
            new Mail(Now.AddDays(-20), Person("Карл", "karl@example.org")),
            new Mail(Now.AddDays(-10), Person("Карл", "karl@example.org")),
            new Mail(Now.AddDays(-1), Person("Клара Иванова", "clara@example.org")),
            new Mail(Now.AddDays(-1), Person("broken", "not-an-address")),
        ]);
        return server;
    }

    [Fact]
    public async Task People_Written_To_Come_Before_People_Heard_From_And_The_Latest_Name_Wins()
    {
        var service = Service(Typical());

        var found = await service.SuggestAsync(Credentials, string.Empty, 8, Token);

        Assert.Equal(["anna@example.org", "boris@example.org", "karl@example.org", "clara@example.org"], found.Select(a => a.Address));
        Assert.Equal("Анна Петрова", found[0].Name); // the name she was last seen under, not the first
    }

    [Fact]
    public async Task The_Own_Address_And_Things_That_Are_Not_Addresses_Are_Never_Offered()
    {
        var service = Service(Typical());

        var all = await AddressesAsync(service, string.Empty);

        Assert.DoesNotContain("me@vinette.test", all);
        Assert.DoesNotContain("not-an-address", all);
        Assert.Empty(await AddressesAsync(service, "me@"));
    }

    [Theory]
    [InlineData("an", new[] { "anna@example.org" })]
    [InlineData("AN", new[] { "anna@example.org" })] // case does not matter
    [InlineData("анн", new[] { "anna@example.org" })] // a name in Cyrillic
    [InlineData("петр", new[] { "anna@example.org" })] // a later word of a name
    [InlineData("анна пет", new[] { "anna@example.org" })] // several words, each starting one
    [InlineData("k", new[] { "karl@example.org" })]
    [InlineData("кла", new[] { "clara@example.org" })]
    [InlineData("zzz", new string[0])]
    public async Task What_Is_Typed_Finds_The_Beginning_Of_An_Address_A_Name_Or_A_Word_Of_A_Name(string query, string[] expected)
    {
        var service = Service(Typical());

        var found = await AddressesAsync(service, query);

        Assert.Equal(expected, found);
    }

    [Fact]
    public async Task A_Beginning_Ranks_Above_A_Word_And_A_Word_Above_Somewhere_Inside_Whatever_The_Score()
    {
        var server = new Server();
        server.Folders["Sent"].AddRange(
        [
            new Mail(Now, null, Person("Ivan Frequent", "x@example.org")), // "van" is inside a word: rank 2, but written to three times
            new Mail(Now, null, Person("Ivan Frequent", "x@example.org")),
            new Mail(Now, null, Person("Ivan Frequent", "x@example.org")),
            new Mail(Now, null, Person("", "chivan@example.org")), // inside the address: rank 2
            new Mail(Now, null, Person("Jan Vanderbilt", "jan@example.org")), // starts a word of the name: rank 1
            new Mail(Now, null, Person("Vanya", "vanya@example.org")), // starts the name: rank 0
        ]);
        var service = Service(server);

        var found = await AddressesAsync(service, "van");

        Assert.Equal(["vanya@example.org", "jan@example.org", "x@example.org", "chivan@example.org"], found);
    }

    [Fact]
    public async Task The_Count_Is_Respected()
    {
        var service = Service(Typical());

        Assert.Single(await AddressesAsync(service, string.Empty, 1));
        Assert.Equal(4, (await AddressesAsync(service, string.Empty, 20)).Length);
    }

    [Fact]
    public async Task A_Mailbox_Without_A_Sent_Folder_Still_Has_Its_Senders()
    {
        var server = Typical();
        server.FolderList = [Folder("INBOX", FolderRole.Inbox)];
        var service = Service(server);

        Assert.Equal(["karl@example.org", "clara@example.org"], await AddressesAsync(service, string.Empty));
    }

    [Fact]
    public async Task Only_The_Newest_Mail_Of_Each_Folder_Is_Read_However_Big_The_Mailbox_Is()
    {
        var server = new Server();
        for (var i = 0; i < 50_000; i++)
        {
            server.Folders["Sent"].Add(new Mail(Now.AddMinutes(-i), null, Person("", $"person{i % 3000}@example.org")));
            server.Folders["INBOX"].Add(new Mail(Now.AddMinutes(-i), Person("", $"sender{i % 3000}@example.org")));
        }

        var service = Service(server);

        var first = Stopwatch.StartNew();
        _ = await service.SuggestAsync(Credentials, "person1", 8, Token);
        first.Stop();

        Assert.Equal(2 * RecipientSuggestionService.ScanMessages, server.Served);

        var repeat = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            _ = await service.SuggestAsync(Credentials, "sender12", 8, Token);
        }

        repeat.Stop();
        Assert.Equal(1, server.Commands); // every later answer came from memory
        Assert.True(repeat.Elapsed.TotalMilliseconds / 20 < 200, $"an answer took {repeat.Elapsed.TotalMilliseconds / 20:F1} ms");
    }

    [Fact]
    public async Task Requests_That_Come_Together_Share_One_Read()
    {
        var server = Typical();
        server.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Service(server);

        var calls = Enumerable.Range(0, 8).Select(_ => service.SuggestAsync(Credentials, "a", 8, Token)).ToList();
        server.Gate.SetResult();
        _ = await Task.WhenAll(calls);

        Assert.Equal(1, server.Commands);
    }

    [Fact]
    public async Task A_Request_That_Is_Given_Up_On_Does_Not_Stop_The_Read_For_The_Next_One()
    {
        var server = Typical();
        server.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Service(server);
        using var cancel = new CancellationTokenSource();

        var first = service.SuggestAsync(Credentials, string.Empty, 8, cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var second = service.SuggestAsync(Credentials, string.Empty, 8, Token);
        server.Gate.SetResult();

        Assert.Equal(4, (await second).Count);
        Assert.Equal(1, server.Commands);
    }

    [Fact]
    public async Task A_Read_That_Fails_Is_Reported_And_Tried_Again_By_The_Next_Request()
    {
        var server = Typical();
        server.Fail = new MailUnavailableException("down");
        var service = Service(server);

        await Assert.ThrowsAsync<MailUnavailableException>(() => service.SuggestAsync(Credentials, string.Empty, 8, Token));
        server.Fail = null;
        await Task.Delay(50, Token); // the failed read lets go of the mailbox

        Assert.Equal(4, (await service.SuggestAsync(Credentials, string.Empty, 8, Token)).Count);
        Assert.Equal(2, server.Commands);
    }

    [Fact]
    public async Task What_Is_Old_Still_Answers_While_It_Is_Read_Again_In_The_Background()
    {
        var server = Typical();
        var clock = new Clock();
        var service = Service(server, clock);
        _ = await service.SuggestAsync(Credentials, string.Empty, 8, Token);
        clock.Utc = Now + RecipientSuggestionService.Lifetime + TimeSpan.FromMinutes(1);
        server.Folders["Sent"].Add(new Mail(clock.Utc, null, Person("Новый", "new@example.org")));

        var stale = await AddressesAsync(service, "new"); // answered at once, from what is kept
        Assert.Empty(stale);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (server.Commands < 2 || (await AddressesAsync(service, "new")).Length == 0)
        {
            await Task.Delay(20, timeout.Token);
        }

        Assert.Equal(["new@example.org"], await AddressesAsync(service, "new"));
        Assert.Equal(2, server.Commands);
    }

    [Fact]
    public async Task A_Message_That_Was_Just_Sent_Is_Offered_Without_Waiting_For_The_Next_Read()
    {
        var service = Service(Typical());
        _ = await service.SuggestAsync(Credentials, string.Empty, 8, Token);

        service.Remember("ME@vinette.test", [Person("Дина", "dina@example.org"), Person("", "me@vinette.test"), Person("", "garbage")]);

        Assert.Equal(["dina@example.org"], await AddressesAsync(service, "ди"));
        Assert.Empty(await AddressesAsync(service, "garbage"));
        service.Remember("nobody@vinette.test", [Person("", "x@example.org")]); // a mailbox with nothing kept: nothing to do
    }

    [Fact]
    public async Task Mailboxes_Do_Not_Share_What_Is_Kept()
    {
        var server = Typical();
        var service = Service(server);
        _ = await service.SuggestAsync(Credentials, string.Empty, 8, Token);

        _ = await service.SuggestAsync(new MailCredentials("t", "other@vinette.test", "pw"), string.Empty, 8, Token);

        Assert.Equal(2, server.Commands);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_Count_Below_One_Is_Refused(int count) =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Service(Typical()).SuggestAsync(Credentials, string.Empty, count, Token));
}
