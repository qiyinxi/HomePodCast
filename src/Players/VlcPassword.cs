using System.Security.Cryptography;
using System.Text;

namespace HomePodCast.Players;

/// <summary>
/// The VLC web-interface password typed in 设置, for when the one in VLC's own settings cannot be found or is
/// refused. config.json only ever holds <see cref="Protect"/>'s result (AppConfig.VlcPasswordProtected): base64
/// of a DPAPI blob for the current Windows user, with HomePodCast's own entropy. It is decrypted each time a
/// request needs it and never kept, logged or shown.
/// </summary>
internal static class VlcPassword
{
    private static ReadOnlySpan<byte> Entropy => "HomePodCast/VLC web interface password/v1"u8;

    /// <summary>Encrypts a password for config.json (throws when DPAPI refuses).</summary>
    public static string Protect(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var plain = Encoding.UTF8.GetBytes(password);
        try
        {
            return Convert.ToBase64String(Dpapi.Protect(plain, Entropy));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>The password from <see cref="Protect"/>'s result; null when there is none or it cannot be decrypted
    /// (config.json copied from another user or PC, or edited).</summary>
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(stored.Trim());
        }
        catch (FormatException)
        {
            return null;
        }
        var plain = Dpapi.Unprotect(blob, Entropy);
        if (plain == null) return null;
        try
        {
            return plain.Length == 0 ? null : Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>Reads the saved password on demand (the setting may change while the app runs).</summary>
    public static Func<string?> From(Func<AppConfig> config) => () => Unprotect(config().VlcPasswordProtected);
}
