using System.Text;
using Microsoft.Extensions.Options;
using Vinette.Api.Configuration;
using Vinette.Api.Scheduling;
using Vinette.Core.Mail;
using Vinette.Core.Scheduling;

namespace Vinette.Api.Tests;

/// <summary>The queue on disk (ADR-0008): a message and its mail are kept, found by mailbox and by time, changed and removed whole, and a damaged or foreign file is not a message.</summary>
public sealed class FileScheduledMailStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly string directory = Path.Combine(Path.GetTempPath(), "vinette-scheduled-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private FileScheduledMailStore Store() => new(Options.Create(new SchedulingOptions { Path = directory }));

    private static ScheduledMail Item(string mailbox = "alice@vinette.test", DateTimeOffset? at = null, ScheduledState state = ScheduledState.Pending) => new(
        Guid.NewGuid().ToString("N"),
        mailbox,
        at ?? Now.AddHours(1),
        Now,
        "Привет",
        [new MailAddressInfo("Анна", "anna@example.org")],
        [],
        [new MailAddressInfo("", "hidden@example.org")],
        "m@vinette.test",
        state,
        ProtectedPassword: "CfDJ8-secret");

    private static readonly System.Text.Json.JsonSerializerOptions Web = new(System.Text.Json.JsonSerializerDefaults.Web);

    private static void AssertSame(ScheduledMail expected, ScheduledMail? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal((expected.Id, expected.Mailbox, expected.SendAt, expected.CreatedAt, expected.Subject, expected.MessageId, expected.State, expected.ProtectedPassword), (actual.Id, actual.Mailbox, actual.SendAt, actual.CreatedAt, actual.Subject, actual.MessageId, actual.State, actual.ProtectedPassword));
        Assert.Equal(expected.To, actual.To);
        Assert.Equal(expected.Cc, actual.Cc);
        Assert.Equal(expected.Bcc, actual.Bcc);
    }

    private static readonly byte[] Mail = Encoding.UTF8.GetBytes("Subject: s" + (char)13 + (char)10 + (char)13 + (char)10 + "body");

    [Fact]
    public async Task What_Is_Added_Is_Found_Whole_By_Its_Mailbox_Even_By_A_New_Store_As_After_A_Restart()
    {
        var item = Item();
        await Store().AddAsync(item, Mail, Token);

        var again = Store();

        AssertSame(item, await again.GetAsync("ALICE@vinette.test", item.Id, Token)); // the address is compared without regard to case
        AssertSame(item, Assert.Single(await again.ListAsync("alice@vinette.test", Token)));
        Assert.Equal(Mail, await again.ReadRawAsync(item.Id, Token));
    }

    [Fact]
    public async Task A_Mailbox_Sees_Only_Its_Own_Soonest_First()
    {
        var store = Store();
        var late = Item(at: Now.AddHours(5));
        var soon = Item(at: Now.AddHours(1));
        await store.AddAsync(late, Mail, Token);
        await store.AddAsync(soon, Mail, Token);
        await store.AddAsync(Item("bob@vinette.test"), Mail, Token);

        var mine = await store.ListAsync("alice@vinette.test", Token);

        Assert.Equal([soon.Id, late.Id], mine.Select(m => m.Id));
        Assert.Null(await store.GetAsync("bob@vinette.test", soon.Id, Token));
    }

    [Fact]
    public async Task What_Is_Due_Is_What_Waits_And_Whose_Time_Or_Next_Try_Has_Come_Of_Every_Mailbox()
    {
        var store = Store();
        var due = Item(at: Now.AddMinutes(-1));
        var otherMailbox = Item("bob@vinette.test", at: Now.AddMinutes(-2));
        var future = Item(at: Now.AddHours(1));
        var retryLater = Item(at: Now.AddMinutes(-30)) with { NextAttempt = Now.AddMinutes(10) };
        var failed = Item(at: Now.AddMinutes(-5), state: ScheduledState.Failed);
        foreach (var item in new[] { due, otherMailbox, future, retryLater, failed })
        {
            await store.AddAsync(item, Mail, Token);
        }

        var found = await store.DueAsync(Now, Token);

        Assert.Equal([otherMailbox.Id, due.Id], found.Select(m => m.Id)); // the one that waited longest first
    }

    [Fact]
    public async Task A_Change_Replaces_The_Description_And_Leaves_The_Mail_And_A_Message_That_Is_Gone_Does_Not_Come_Back()
    {
        var store = Store();
        var item = Item();
        await store.AddAsync(item, Mail, Token);

        var changed = await store.UpdateAsync(item with { Attempts = 3 }, Token);
        Assert.True(changed);
        Assert.Equal(3, (await store.GetAsync(item.Mailbox, item.Id, Token))!.Attempts);
        Assert.Equal(Mail, await store.ReadRawAsync(item.Id, Token));

        _ = await store.RemoveAsync(item.Mailbox, item.Id, Token);
        Assert.False(await store.UpdateAsync(item with { Attempts = 9 }, Token));
        Assert.Empty(await store.ListAsync(item.Mailbox, Token));
    }

    [Fact]
    public async Task Removing_Takes_The_Description_The_Mail_And_The_Password_Of_Only_The_Owner()
    {
        var store = Store();
        var item = Item();
        await store.AddAsync(item, Mail, Token);

        Assert.False(await store.RemoveAsync("bob@vinette.test", item.Id, Token)); // not his
        Assert.True(await store.RemoveAsync("Alice@Vinette.test", item.Id, Token));

        Assert.Empty(Directory.EnumerateFiles(directory));
        Assert.False(await store.RemoveAsync(item.Mailbox, item.Id, Token));
        Assert.Null(await store.ReadRawAsync(item.Id, Token));
    }

    [Fact]
    public async Task Dropping_The_Mail_Keeps_The_Note()
    {
        var store = Store();
        var item = Item();
        await store.AddAsync(item, Mail, Token);

        await store.DropRawAsync(item.Id, Token);

        Assert.Null(await store.ReadRawAsync(item.Id, Token));
        Assert.NotNull(await store.GetAsync(item.Mailbox, item.Id, Token));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789")]
    [InlineData("0123456789abcdef0123456789abcde")]
    public async Task An_Id_That_Is_Not_32_Lower_Case_Hex_Digits_Never_Touches_A_Path(string id)
    {
        var store = Store();

        Assert.Null(await store.GetAsync("alice@vinette.test", id, Token));
        Assert.Null(await store.ReadRawAsync(id, Token));
        Assert.False(await store.RemoveAsync("alice@vinette.test", id, Token));
        await store.DropRawAsync(id, Token);
        _ = await Assert.ThrowsAsync<ArgumentException>(() => store.AddAsync(Item() with { Id = id }, Mail, Token));
    }

    [Fact]
    public async Task A_Damaged_Or_Foreign_Or_Huge_File_Is_Not_A_Message_And_Does_Not_Stop_The_Others()
    {
        var store = Store();
        var good = Item();
        await store.AddAsync(good, Mail, Token);
        var foreignId = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(Path.Combine(directory, foreignId + ".json"), "{ not json", Token);
        var other = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(Path.Combine(directory, other + ".json"), System.Text.Json.JsonSerializer.Serialize(good, Web), Token); // a description under another name
        var huge = Guid.NewGuid().ToString("N");
        await File.WriteAllBytesAsync(Path.Combine(directory, huge + ".json"), new byte[300 * 1024], Token);

        var found = await store.ListAsync(good.Mailbox, Token);

        Assert.Equal([good.Id], found.Select(m => m.Id));
        Assert.Equal([good.Id], (await store.DueAsync(Now.AddDays(1), Token)).Select(m => m.Id));
    }

    [Fact]
    public async Task Nothing_Half_Written_Is_Left_Behind()
    {
        var store = Store();
        await store.AddAsync(Item(), Mail, Token);

        Assert.DoesNotContain(Directory.EnumerateFiles(directory), f => f.EndsWith(".tmp", StringComparison.Ordinal));
        Assert.Equal(2, Directory.EnumerateFiles(directory).Count()); // the description and the mail
    }

    [Fact]
    public async Task Changes_Made_At_The_Same_Time_Do_Not_Lose_Each_Other_Or_Damage_The_File()
    {
        var store = Store();
        var item = Item();
        await store.AddAsync(item, Mail, Token);

        await Task.WhenAll(Enumerable.Range(0, 30).Select(i => store.UpdateAsync(item with { Attempts = i }, Token)));

        var after = await store.GetAsync(item.Mailbox, item.Id, Token);
        Assert.NotNull(after); // whole, whichever change came last
        Assert.InRange(after.Attempts, 0, 29);
    }
}
