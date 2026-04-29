using System.Security.Cryptography;
using System.Text;

namespace Poseidon.Desktop.Diagnostics;

public static class RuntimeSecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Poseidon-LCDSS-DPAPI-v1");

    public static string Protect(string secret)
    {
        if (string.IsNullOrEmpty(secret))
            return "";

        var bytes = Encoding.UTF8.GetBytes(secret);
        var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(bytes);
        return Convert.ToBase64String(protectedBytes);
    }

    public static string? TryUnprotect(string? protectedSecret)
    {
        if (string.IsNullOrWhiteSpace(protectedSecret))
            return null;

        try
        {
            var bytes = Convert.FromBase64String(protectedSecret);
            var plaintext = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch
        {
            return null;
        }
    }
}
