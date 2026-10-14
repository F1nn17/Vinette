using System.Runtime.CompilerServices;
using System.Text;

namespace Vinette.Mail.Tests;

internal static class TestSetup
{
    /// <summary>
    /// koi8-r, windows-1251 and other legacy charsets are not built into .NET; MimeKit needs the code pages provider.
    /// The application must register it at startup as well (see VIN-055).
    /// </summary>
    [ModuleInitializer]
    internal static void RegisterCodePages() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
}
