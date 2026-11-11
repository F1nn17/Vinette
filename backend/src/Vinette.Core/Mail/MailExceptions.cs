namespace Vinette.Core.Mail;

/// <summary>The mail server could not be reached or stopped answering (network, timeout, TLS). Retrying later may help.</summary>
public sealed class MailUnavailableException : Exception
{
    public MailUnavailableException()
    {
    }

    public MailUnavailableException(string message)
        : base(message)
    {
    }

    public MailUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The mail server no longer accepts the stored password (it was changed or access was revoked). The web session
/// cannot work any more and must be ended.
/// </summary>
public sealed class MailAccessRevokedException : Exception
{
    public MailAccessRevokedException()
    {
    }

    public MailAccessRevokedException(string message)
        : base(message)
    {
    }

    public MailAccessRevokedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Too many mail connections are open (per user or in total) and none became free in time. Unlike
/// <see cref="MailUnavailableException"/> the mail server is fine: the application is busy. Trying again shortly may work.
/// </summary>
public sealed class MailBusyException : Exception
{
    public MailBusyException()
    {
    }

    public MailBusyException(string message)
        : base(message)
    {
    }

    public MailBusyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public MailBusyException(string message, TimeSpan retryAfter)
        : base(message) => RetryAfter = retryAfter;

    /// <summary>When to try again.</summary>
    public TimeSpan RetryAfter { get; } = TimeSpan.FromSeconds(5);
}

/// <summary>The folder does not exist (or cannot hold messages). Not a server problem: the request names something that is not there.</summary>
public sealed class MailFolderNotFoundException : Exception
{
    public MailFolderNotFoundException()
    {
    }

    public MailFolderNotFoundException(string message)
        : base(message)
    {
    }

    public MailFolderNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
