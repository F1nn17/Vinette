using Vinette.Api.Mail;
using Vinette.Core.Mail;

namespace Vinette.Api.Hubs;

/// <summary>
/// What the real-time channel sends (the client method <c>mailEvent</c>): one JSON object whose <c>type</c> says which of the
/// events below it is. Only what the screen needs: no passwords, and nothing from a message but the line the list shows.
/// </summary>
/// <param name="Type"><c>arrived</c>, <c>removed</c>, <c>marks</c>, <c>counters</c> or <c>stale</c>.</param>
/// <param name="Folder">The folder's path.</param>
public abstract record MailEventDto(string Type, string Folder);

/// <summary>A message arrived.</summary>
/// <param name="Folder">The folder it arrived in.</param>
/// <param name="Message">The line of the list.</param>
public sealed record ArrivedEventDto(string Folder, MessageListItemDto Message) : MailEventDto("arrived", Folder);

/// <summary>Messages were removed from the folder; only how many is known, the list must look again.</summary>
/// <param name="Folder">The folder.</param>
/// <param name="Count">How many.</param>
public sealed record RemovedEventDto(string Folder, int Count) : MailEventDto("removed", Folder);

/// <summary>The marks of a message changed.</summary>
/// <param name="Folder">The folder.</param>
/// <param name="Id">The message's identifier.</param>
/// <param name="Seen">Read.</param>
/// <param name="Flagged">Marked with a flag.</param>
/// <param name="Answered">Replied to.</param>
/// <param name="Labels">Its labels now.</param>
public sealed record MarksEventDto(string Folder, string Id, bool Seen, bool Flagged, bool Answered, IReadOnlyList<string> Labels) : MailEventDto("marks", Folder);

/// <summary>The counters of the folder changed.</summary>
/// <param name="Folder">The folder.</param>
/// <param name="Total">Messages in it.</param>
/// <param name="Unread">Unread messages in it.</param>
public sealed record CountersEventDto(string Folder, int Total, int Unread) : MailEventDto("counters", Folder);

/// <summary>Whatever the client knows of the folder may be out of date: read it again.</summary>
/// <param name="Folder">The folder.</param>
public sealed record StaleEventDto(string Folder) : MailEventDto("stale", Folder);

public static class MailEventMapping
{
    /// <summary>The wire form of an event.</summary>
    public static MailEventDto ToDto(MailboxEvent change) => change switch
    {
        MessageArrived e => new ArrivedEventDto(e.Folder, MessageListEndpoints.ToDto(e.Message)),
        MessagesRemoved e => new RemovedEventDto(e.Folder, e.Count),
        MessageMarksChanged e => new MarksEventDto(e.Folder, e.Id.ToString(), e.Seen, e.Flagged, e.Answered, e.Labels),
        FolderCountersChanged e => new CountersEventDto(e.Folder, e.Total, e.Unread),
        FolderStale e => new StaleEventDto(e.Folder),
        _ => throw new ArgumentOutOfRangeException(nameof(change), change.GetType().Name, "Unknown mailbox event."),
    };
}
