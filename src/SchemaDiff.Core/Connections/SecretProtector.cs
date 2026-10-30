using System.Security.Cryptography;
using System.Text;

namespace SchemaDiff.Core.Connections;

/// <summary>Protects secrets such as saved passwords before they are written to disk.</summary>
public interface ISecretProtector
{
    /// <summary>Returns the protected form of the secret, or null when it cannot be protected and must not be stored.</summary>
    string? Protect(string secret);

    /// <summary>Returns the original secret, or null when it cannot be recovered.</summary>
    string? Unprotect(string protectedValue);
}

/// <summary>
/// Windows DPAPI bound to the current user account. Another account cannot decrypt the value even
/// if it can read the file. On other platforms nothing is stored, because writing the password in
/// plain text would be worse than asking for it again.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SchemaDiff.RecentConnections.v1");

    public string? Protect(string secret)
    {
        if (!OperatingSystem.IsWindows()) return null;

        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public string? Unprotect(string protectedValue)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Protected by another user account or not a valid value, so it cannot be recovered.
            return null;
        }
    }
}
