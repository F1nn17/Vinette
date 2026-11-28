using System.Diagnostics;
using Vinette.Core;
using Vinette.Core.Mail;
using Vinette.TestSupport;

namespace Vinette.Mail.Tests;

/// <summary>
/// A mailbox of fifty thousand messages against a real Dovecot (VIN-131): what a reader waits for when the list, the counters, a filter or a search
/// meets a big mailbox. The numbers are the budgets of docs/performance.md; a run on the machine of the project is listed there. The budgets are about
/// three times what was measured, so that a slower machine passes and a change that makes something ten times slower does not.
/// </summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class LargeMailboxIntegrationTests(DovecotFixture dovecot)
{
    private const int Messages = 50_000;

    /// <summary>The messages of <see cref="DovecotFixture.FillInboxAsync"/>: every 10th unread, 97th flagged, 1999th "needle" in the subject, 997th in the text.</summary>
    private const int Unread = Messages / 10;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>One measured operation: what it is, how long it took, and the longest it may take.</summary>
    private sealed record Measure(string Name, TimeSpan Took, TimeSpan Budget);

    private static async Task<(T Result, Measure Measure)> TimeAsync<T>(string name, double budgetSeconds, Func<Task<T>> action)
    {
        var watch = Stopwatch.StartNew();
        var result = await action();
        watch.Stop();
        return (result, new Measure(name, watch.Elapsed, TimeSpan.FromSeconds(budgetSeconds)));
    }

    [Fact]
    public async Task A_Mailbox_Of_Fifty_Thousand_Messages_Is_Listed_Counted_Filtered_And_Searched_Within_The_Budgets()
    {
        var account = await dovecot.CreateAccountAsync($"big{Guid.NewGuid():N}"[..14], Token);
        var filling = Stopwatch.StartNew();
        await dovecot.FillInboxAsync(account, Messages, Token);
        filling.Stop();

        var factory = new ImapMailSessionFactory(new ImapConnectionSettings(dovecot.Host, dovecot.ImapPort, ConnectionSecurity.None, TimeSpan.FromMinutes(2)));
        await using var session = await factory.OpenAsync(new MailCredentials("s", account.Address, account.Password), Token);
        var measures = new List<Measure>();

        // The first look at a folder is the dearest: Dovecot has to build its index of the folder.
        var (first, m1) = await TimeAsync("first page, first look at the folder (previews)", 30, () => session.ListMessagesAsync(new MessageListQuery("INBOX", null, 50), Token));
        measures.Add(m1);
        Assert.Equal(Messages, first.Total);
        Assert.Equal(50, first.Items.Count);
        Assert.Equal((uint)Messages, first.Items[0].Id.Uid); // the newest first

        var (_, m2) = await TimeAsync("first page, no previews", 2, () => session.ListMessagesAsync(new MessageListQuery("INBOX", null, 50, IncludePreview: false), Token));
        measures.Add(m2);
        var (withPreviews, m3) = await TimeAsync("first page, with previews", 2, () => session.ListMessagesAsync(new MessageListQuery("INBOX", null, 50), Token));
        measures.Add(m3);
        Assert.All(withPreviews.Items, i => Assert.False(string.IsNullOrEmpty(i.Preview)));

        var (deep, m4) = await TimeAsync("a page deep in the folder (cursor near the oldest)", 4, () => session.ListMessagesAsync(new MessageListQuery("INBOX", new MessageCursor(first.UidValidity, 120), 50, IncludePreview: false), Token));
        measures.Add(m4);
        Assert.Equal(50, deep.Items.Count);
        Assert.Equal(119u, deep.Items[0].Id.Uid);

        var (middle, m5) = await TimeAsync("a page in the middle of the folder", 4, () => session.ListMessagesAsync(new MessageListQuery("INBOX", new MessageCursor(first.UidValidity, 25_000), 50, IncludePreview: false), Token));
        measures.Add(m5);
        Assert.Equal(24_999u, middle.Items[0].Id.Uid);

        var (folders, m6) = await TimeAsync("the list of folders with their counters", 4, () => session.GetFoldersAsync(Token));
        measures.Add(m6);
        var inbox = folders.Single(f => f.Role == FolderRole.Inbox);
        Assert.Equal(Messages, inbox.Total);
        Assert.Equal(Unread, inbox.Unread);

        var (unread, m7) = await TimeAsync("first page of the unread", 4, () => session.ListMessagesAsync(new MessageListQuery("INBOX", null, 50, IncludePreview: false, Filter: MessageFilter.Unread), Token));
        measures.Add(m7);
        Assert.Equal(Unread, unread.Total);
        Assert.All(unread.Items, i => Assert.False(i.Seen));

        var (flagged, m8) = await TimeAsync("first page of the flagged", 4, () => session.ListMessagesAsync(new MessageListQuery("INBOX", null, 50, IncludePreview: false, Filter: MessageFilter.Flagged), Token));
        measures.Add(m8);
        Assert.All(flagged.Items, i => Assert.True(i.Flagged));

        var (bySubject, m9) = await TimeAsync("search in the subjects (one rare word)", 8, () => session.ListMessagesAsync(new MessageListQuery("INBOX", null, 50, IncludePreview: false, Terms: ["needle"], Scope: MessageSearchScope.Subject), Token));
        measures.Add(m9);
        Assert.Equal(Messages / 1999, bySubject.Total);

        var (bySender, m10) = await TimeAsync("search by sender", 6, () => session.ListMessagesAsync(new MessageListQuery("INBOX", null, 50, IncludePreview: false, Terms: ["sender42@example.org"], Scope: MessageSearchScope.From), Token));
        measures.Add(m10);
        Assert.Equal(Messages / 200, bySender.Total);

        var (byText, m11) = await TimeAsync("search in the text (one rare word, no full-text index)", 15, () => session.ListMessagesAsync(new MessageListQuery("INBOX", null, 50, IncludePreview: false, Terms: ["needle"], Scope: MessageSearchScope.Body), Token));
        measures.Add(m11);
        Assert.Equal(Messages / 997, byText.Total);

        var (russian, m12) = await TimeAsync("search in the text for a Russian word", 15, () => session.ListMessagesAsync(new MessageListQuery("INBOX", null, 50, IncludePreview: false, Terms: ["годовой"], Scope: MessageSearchScope.Body), Token));
        measures.Add(m12);
        Assert.Equal(Messages / 500, russian.Total);

        var (opened, m13) = await TimeAsync("open one message", 4, () => session.GetMessageAsync(first.Items[0].Id, Token));
        measures.Add(m13);
        Assert.NotEmpty(opened.Content);

        var table = string.Join(
            Environment.NewLine,
            measures.Select(m => $"{m.Took.TotalMilliseconds,9:F0} ms  (budget {m.Budget.TotalSeconds,3:F0} s)  {m.Name}"));
        TestContext.Current.SendDiagnosticMessage($"Large mailbox ({Messages} messages; filling it took {filling.Elapsed.TotalSeconds:F1} s):{Environment.NewLine}{table}");

        // `VINETTE_PERF_REPORT=<file>` keeps the table, for writing the numbers down in docs/performance.md.
        if (Environment.GetEnvironmentVariable("VINETTE_PERF_REPORT") is { Length: > 0 } report)
        {
            await File.WriteAllTextAsync(report, $"Large mailbox ({Messages} messages; filling it took {filling.Elapsed.TotalSeconds:F1} s):{Environment.NewLine}{table}{Environment.NewLine}", Token);
        }

        var over = measures.Where(m => m.Took > m.Budget).Select(m => $"{m.Name}: {m.Took.TotalSeconds:F1} s > {m.Budget.TotalSeconds:F0} s").ToList();
        Assert.True(over.Count == 0, "Over the budget:" + Environment.NewLine + string.Join(Environment.NewLine, over) + Environment.NewLine + "All:" + Environment.NewLine + table);
    }
}
