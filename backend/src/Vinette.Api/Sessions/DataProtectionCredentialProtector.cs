using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Vinette.Core.Sessions;

namespace Vinette.Api.Sessions;

/// <summary>
/// Encrypts the mailbox password with ASP.NET Core Data Protection under a dedicated purpose.
/// Decryption needs the same key ring: lose the keys (DataProtection:KeysPath) and every session is lost.
/// </summary>
public sealed class DataProtectionCredentialProtector(IDataProtectionProvider provider) : ICredentialProtector
{
    /// <summary>The purpose isolates these payloads from any other use of the key ring. Change the suffix to invalidate all sessions.</summary>
    internal const string Purpose = "Vinette.Session.Password.v1";

    private readonly IDataProtector protector = provider.CreateProtector(Purpose);

    public ReadOnlyMemory<byte> Protect(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        return protector.Protect(Encoding.UTF8.GetBytes(password));
    }

    public string Unprotect(ReadOnlyMemory<byte> protectedPassword) =>
        Encoding.UTF8.GetString(protector.Unprotect(protectedPassword.ToArray()));
}
