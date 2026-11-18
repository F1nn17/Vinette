using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Vinette.Api.Mail;
using Vinette.Core;
using Vinette.Core.Mail;
using Vinette.Mail;
using Vinette.TestSupport;

namespace Vinette.Api.Tests;

/// <summary>Suggestions against a real Dovecot (VIN-095): the people are found in the real "Sent" folder and inbox, under the names they wrote with.</summary>
[Integration]
[Collection(DovecotFixture.CollectionName)]
public sealed class RecipientSuggestionsIntegrationTests(DovecotFixture dovecot) : IAsyncDisposable
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

    private static MailCredentials Credentials(MailAccount account) => new("session-" + account.Address, account.Address, account.Password);

    private static byte[] Mail(string from, string to, string subject) =>
        Encoding.UTF8.GetBytes($"From: {from}\r\nTo: {to}\r\nSubject: {subject}\r\nDate: Tue, 17 Nov 2026 09:00:00 +0000\r\nMessage-ID: <{Guid.NewGuid():N}@example.org>\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\n\r\ntext\r\n");

    [Fact]
    public async Task People_Are_Found_In_The_Sent_Folder_And_The_Inbox_With_The_Names_They_Use()
    {
        var alice = await dovecot.CreateAccountAsync($"sug{Guid.NewGuid():N}"[..14], Token);
        var factory = new ImapMailSessionFactory(new ImapConnectionSettings(dovecot.Host, dovecot.ImapPort, ConnectionSecurity.None, TimeSpan.FromSeconds(30)));
        var gateway = new MailConnectionManager(factory);
        managers.Add(gateway);
        var credentials = Credentials(alice);
        var folders = FolderRoleGuesser.Complete(await gateway.ExecuteAsync(credentials, (session, token) => session.GetFoldersAsync(token), Token));
        var sent = folders.Single(f => f.Role == FolderRole.Sent).Path;
        var date = new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);
        await gateway.ExecuteAsync(
            credentials,
            async (session, token) =>
            {
                _ = await session.AppendMessageAsync(sent, Mail(alice.Address, "=?utf-8?B?0JDQvdC90LAg0J/QtdGC0YDQvtCy0LA=?= <anna@example.org>, boris@example.org", "Привет"), true, false, date, token);
                _ = await session.AppendMessageAsync("INBOX", Mail("Carol Lee <carol@example.org>", alice.Address, "Hello"), false, false, date, token);
                return 0;
            },
            Token);
        var service = new RecipientSuggestionService(gateway, NullLogger<RecipientSuggestionService>.Instance);

        var all = await service.SuggestAsync(credentials, string.Empty, 8, Token);
        var byName = await service.SuggestAsync(credentials, "пет", 8, Token);

        Assert.Equal(["anna@example.org", "boris@example.org", "carol@example.org"], all.Select(a => a.Address));
        Assert.Equal("Анна Петрова", all[0].Name); // a name in Cyrillic, decoded
        Assert.Equal("Carol Lee", all[2].Name);
        Assert.Equal(["anna@example.org"], byName.Select(a => a.Address));
        Assert.DoesNotContain(all, a => a.Address == alice.Address);
    }
}
