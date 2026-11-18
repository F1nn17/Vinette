using Vinette.Api.Mail;
using Vinette.Core;
using Vinette.Core.Mail;
using Vinette.Mail;
using Vinette.TestSupport;

namespace Vinette.Api.Tests;

/// <summary>Drafts against a real Dovecot (VIN-094): a draft is a flagged mail in the drafts folder, saving again replaces it, and it opens with its files.</summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class DraftsIntegrationTests(DovecotFixture dovecot) : IAsyncDisposable
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

    private async Task<MailAccount> AccountAsync() =>
        await dovecot.CreateAccountAsync($"drf{Guid.NewGuid():N}"[..14], Token);

    private static MailCredentials Credentials(MailAccount account) => new("session-" + account.Address, account.Address, account.Password);

    private (DraftService Service, MailConnectionManager Gateway) Service()
    {
        var factory = new ImapMailSessionFactory(new ImapConnectionSettings(dovecot.Host, dovecot.ImapPort, ConnectionSecurity.None, TimeSpan.FromSeconds(30)));
        var gateway = new MailConnectionManager(factory);
        managers.Add(gateway);
        return (new DraftService(new MimeMessageComposer(), new MimeMessageParser(), gateway), gateway);
    }

    private static OutgoingMessage Message(MailAccount from, string subject, string text, params OutgoingAttachment[] files) =>
        new(new MailAddressInfo("Алиса", from.Address), [], [], [new MailAddressInfo("", "hidden@example.org")], subject, text, null, files);

    private static async Task<MessageListItemSnapshot> DraftsAsync(MailConnectionManager gateway, MailAccount account)
    {
        var folders = FolderRoleGuesser.Complete(await gateway.ExecuteAsync(Credentials(account), (session, token) => session.GetFoldersAsync(token), Token));
        var drafts = Assert.Single(folders, f => f.Role == FolderRole.Drafts);
        var listed = await gateway.ExecuteAsync(Credentials(account), (session, token) => session.ListMessagesAsync(new MessageListQuery(drafts.Path, null, 50), token), Token);
        return new MessageListItemSnapshot(drafts.Path, listed.Items.Count);
    }

    private sealed record MessageListItemSnapshot(string Folder, int Count);

    [Fact]
    public async Task A_Draft_Is_A_Flagged_Read_Mail_In_The_Drafts_Folder_That_Opens_As_It_Was_Written_With_Its_Files()
    {
        var alice = await AccountAsync();
        var (service, gateway) = Service();
        var file = new OutgoingAttachment("смета.pdf", "application/pdf", Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray());

        var saved = await service.SaveAsync(Credentials(alice), Message(alice, "Черновик письма", "Начало текста", file), null, Token);

        Assert.NotNull(saved?.Id);
        var id = saved.Id.Value;
        var source = await gateway.ExecuteAsync(Credentials(alice), (session, token) => session.GetMessageAsync(id, token), Token);
        Assert.True(source.Draft);
        Assert.True(source.Seen);
        var drafts = await DraftsAsync(gateway, alice);
        Assert.Equal(drafts.Folder, id.Folder);
        Assert.Equal(1, drafts.Count);

        var opened = await service.OpenAsync(Credentials(alice), id, Token);

        Assert.NotNull(opened);
        Assert.Equal("Черновик письма", opened.Message.Subject);
        Assert.Contains("Начало текста", opened.Message.Text, StringComparison.Ordinal);
        Assert.Empty(opened.Message.To);
        Assert.Equal(["hidden@example.org"], opened.Message.Bcc.Select(a => a.Address));
        var back = Assert.Single(opened.Message.Attachments);
        Assert.Equal("смета.pdf", back.FileName);
        Assert.Equal(file.Content.ToArray(), back.Content.ToArray());
    }

    [Fact]
    public async Task Saving_Again_Replaces_The_Draft_So_The_Folder_Never_Holds_Two_Versions()
    {
        var alice = await AccountAsync();
        var (service, gateway) = Service();
        var first = await service.SaveAsync(Credentials(alice), Message(alice, "Версия", "один"), null, Token);

        var second = await service.SaveAsync(Credentials(alice), Message(alice, "Версия", "два") with { MessageId = first!.MessageId }, first.Id, Token);

        Assert.True(second!.PreviousRemoved);
        Assert.Equal(first.MessageId, second.MessageId);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(1, (await DraftsAsync(gateway, alice)).Count);
        await Assert.ThrowsAsync<MailMessageNotFoundException>(() => service.OpenAsync(Credentials(alice), first.Id!.Value, Token));
        var opened = await service.OpenAsync(Credentials(alice), second.Id!.Value, Token);
        Assert.Contains("два", opened!.Message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Draft_Is_Deleted_For_Good_And_Another_Folder_Cannot_Be_Reached_Through_Drafts()
    {
        var alice = await AccountAsync();
        var (service, gateway) = Service();
        var saved = await service.SaveAsync(Credentials(alice), Message(alice, "Лишнее", "текст"), null, Token);
        var inbox = await gateway.ExecuteAsync(Credentials(alice), (session, token) => session.ListMessagesAsync(new MessageListQuery("INBOX", null, 50), token), Token);
        Assert.Empty(inbox.Items);

        Assert.True(await service.DeleteAsync(Credentials(alice), saved!.Id!.Value, Token));

        Assert.Equal(0, (await DraftsAsync(gateway, alice)).Count);
        await Assert.ThrowsAsync<MailMessageNotFoundException>(() => service.DeleteAsync(Credentials(alice), saved.Id.Value, Token));
        await Assert.ThrowsAsync<MailMessageNotFoundException>(() => service.DeleteAsync(Credentials(alice), new MessageId("INBOX", 1, 1), Token));
    }
}
