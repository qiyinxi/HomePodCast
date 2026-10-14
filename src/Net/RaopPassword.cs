using System.Security.Cryptography;
using System.Text;

namespace HomePodCast.Net;

/// <summary>Device-bound DPAPI passwords; never leave this Windows user account.</summary>
public static class RaopPassword
{
    private static byte[] Entropy(string deviceId) => Encoding.UTF8.GetBytes("HomePodCast/RAOP password/v1/" + Key(deviceId));
    public static string Key(string id) => id.Replace(":", "").Replace("-", "").Trim().ToUpperInvariant();
    public static string Protect(string deviceId, string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var plain = Encoding.UTF8.GetBytes(password);
        try { return Convert.ToBase64String(Dpapi.Protect(plain, Entropy(deviceId))); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public static string? Unprotect(string deviceId, string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        byte[] blob;
        try { blob = Convert.FromBase64String(stored); } catch (FormatException) { return null; }
        var plain = Dpapi.Unprotect(blob, Entropy(deviceId));
        if (plain == null) return null;
        try { return plain.Length == 0 ? null : Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
