namespace Vinette.Mail;

/// <summary>How much of the server's delta-sync (finding out what changed since a point in time) a watch of the inbox may use.</summary>
internal enum DeltaSync
{
    /// <summary>QRESYNC if the server has it (it names what vanished and what changed), else CONDSTORE, else nothing.</summary>
    Full,

    /// <summary>CONDSTORE only (the server names what changed; removals are counted).</summary>
    CondStoreOnly,

    /// <summary>Neither: arrivals are found by UID, everything else is "stale".</summary>
    None,
}
