namespace Vinette.Core.Sessions;

/// <summary>Encrypts the mailbox password for storage (ASP.NET Core Data Protection in Vinette.Api).</summary>
public interface ICredentialProtector
{
    ReadOnlyMemory<byte> Protect(string password);

    /// <exception cref="System.Security.Cryptography.CryptographicException">The data is corrupted or the key is gone.</exception>
    string Unprotect(ReadOnlyMemory<byte> protectedPassword);
}
