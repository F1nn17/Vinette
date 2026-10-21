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
