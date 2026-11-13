namespace Vinette.Core.Mail;

/// <summary>
/// What a watch of the inbox knows about it, kept by the watcher from one watching connection to the next. A connection that is
/// lost and opened again starts from this instead of from nothing, so that what happened meanwhile (mail that arrived, messages
/// that were removed or marked) is found out and reported (VIN-086) instead of being missed. The session fills it in as it goes.
/// </summary>
public sealed class MailboxWatchState
{
    /// <summary>The state was filled in by an earlier connection.</summary>
    public bool Known { get; set; }

    /// <summary>The folder's <c>UIDVALIDITY</c> then; another value now means a different folder, and nothing carries over.</summary>
    public uint UidValidity { get; set; }

    /// <summary>The server's mod-sequence of the folder then (CONDSTORE); null if the server has none.</summary>
    public ulong? HighestModSeq { get; set; }

    /// <summary>The highest UID there was: mail with a higher one arrived since.</summary>
    public uint LastUid { get; set; }

    /// <summary>How many messages the folder had.</summary>
    public int Total { get; set; }

    /// <summary>How many of them were unread.</summary>
    public int Unread { get; set; }
}
