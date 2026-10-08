namespace Vinette.Mail;

/// <summary>Marker for the assembly that holds the MailKit-based IMAP/SMTP implementation.</summary>
public static class MailAssembly
{
    public static string Name => typeof(MailAssembly).Assembly.GetName().Name!;
}
