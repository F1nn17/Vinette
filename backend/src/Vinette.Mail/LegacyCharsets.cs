using System.Text;

namespace Vinette.Mail;

/// <summary>
/// koi8-r, windows-1251 and the other legacy charsets are not built into .NET: without the code pages provider MimeKit
/// cannot decode headers and bodies written in them (the mail list, the preview and the parser all depend on it).
/// </summary>
internal static class LegacyCharsets
{
    private static readonly object Gate = new();
    private static bool registered;

    /// <summary>Registers the provider once; safe to call from anywhere, any number of times.</summary>
    internal static void Ensure()
    {
        if (registered)
        {
            return;
        }

        lock (Gate)
        {
            if (!registered)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                registered = true;
            }
        }
    }
}
