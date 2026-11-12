namespace Vinette.Core.Mail;

/// <summary>Limits of actions on several messages at once.</summary>
public static class MessageBatch
{
    /// <summary>The most messages one action takes (one request, one command to the mail server).</summary>
    public const int MaxMessages = 100;

    /// <summary>
    /// Checks that <paramref name="ids"/> can be one batch: 1 to <see cref="MaxMessages"/> messages, all of one folder as it is
    /// now (one <c>UIDVALIDITY</c>), none twice.
    /// </summary>
    public static bool IsValid(IReadOnlyCollection<MessageId> ids, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(ids);
        problem = ids.Count switch
        {
            0 => "Name at least one message.",
            > MaxMessages => $"At most {MaxMessages} messages can be changed at once.",
            _ when ids.Select(i => (i.Folder, i.UidValidity)).Distinct().Count() > 1 => "All the messages must be in one folder.",
            _ when ids.Distinct().Count() != ids.Count => "A message is named twice.",
            _ => null,
        };
        return problem is null;
    }
}

/// <summary>A message that was moved: where it was, and its id in the new folder if the mail server says it (UIDPLUS).</summary>
public sealed record MovedMessage(MessageId From, MessageId? To);
