namespace Vinette.Core.Mail;

/// <summary>
/// Something that happened in a watched folder, in the terms of the screen (not of the mail protocol): what the list and the
/// counters have to show. The watching connection makes these out of the server's notifications (VIN-081, VIN-082).
/// </summary>
/// <param name="Folder">The folder's path (only the inbox is watched for now).</param>
public abstract record MailboxEvent(string Folder);

/// <summary>A message arrived. Carries the line of the list, so that it can be shown (and announced) without asking again.</summary>
/// <param name="Folder">The folder it arrived in.</param>
/// <param name="Message">The message as the list shows it.</param>
public sealed record MessageArrived(string Folder, MessageListItem Message) : MailboxEvent(Folder);

/// <summary>
/// Messages were removed from the folder (deleted, moved away). The server says positions, not identifiers, so only how many is
/// known: the list must look again.
/// </summary>
/// <param name="Folder">The folder they were removed from.</param>
/// <param name="Count">How many.</param>
public sealed record MessagesRemoved(string Folder, int Count) : MailboxEvent(Folder);

/// <summary>The marks of a message changed (read, flag, replied to, labels), here or in another mail program.</summary>
/// <param name="Folder">The folder the message is in.</param>
/// <param name="Id">The message.</param>
/// <param name="Seen">Read.</param>
/// <param name="Flagged">Marked with a flag.</param>
/// <param name="Answered">Replied to.</param>
/// <param name="Labels">The message's labels now (by their names, system marks left out).</param>
public sealed record MessageMarksChanged(string Folder, MessageId Id, bool Seen, bool Flagged, bool Answered, IReadOnlyList<string> Labels) : MailboxEvent(Folder);

/// <summary>The counters of the folder changed.</summary>
/// <param name="Folder">The folder.</param>
/// <param name="Total">Messages in it.</param>
/// <param name="Unread">Unread messages in it.</param>
public sealed record FolderCountersChanged(string Folder, int Total, int Unread) : MailboxEvent(Folder);

/// <summary>
/// The folder changed in a way the other events cannot describe (a lot of mail at once, a change of marks without an identifier):
/// whatever the screen holds of it may be out of date and must be read again.
/// </summary>
/// <param name="Folder">The folder.</param>
public sealed record FolderStale(string Folder) : MailboxEvent(Folder);
