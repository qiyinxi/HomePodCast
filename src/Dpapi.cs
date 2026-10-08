using System.ComponentModel;
using System.Runtime.InteropServices;

namespace HomePodCast;

/// <summary>
/// Windows DPAPI for the current user (crypt32 CryptProtectData / CryptUnprotectData): only the same Windows
/// account on the same PC can decrypt a blob. Callers pass an app-specific entropy so another program running
/// as the same user cannot simply hand the blob to DPAPI without knowing it. No UI is ever shown.
/// </summary>
internal static unsafe partial class Dpapi
{
    private const uint UiForbidden = 0x1; // CRYPTPROTECT_UI_FORBIDDEN

    /// <summary>Encrypts <paramref name="data"/>; throws <see cref="Win32Exception"/> when DPAPI refuses.</summary>
    public static byte[] Protect(ReadOnlySpan<byte> data, ReadOnlySpan<byte> entropy)
    {
        fixed (byte* pData = data)
        fixed (byte* pEntropy = entropy)
        {
            var input = new Blob(data.Length, pData);
            var extra = new Blob(entropy.Length, pEntropy);
            if (!CryptProtectData(in input, IntPtr.Zero, in extra, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            try
            {
                return new ReadOnlySpan<byte>(output.Data, output.Size).ToArray();
            }
            finally
            {
                LocalFree((IntPtr)output.Data);
            }
        }
    }

    /// <summary>
    /// Decrypts a blob made by <see cref="Protect"/> with the same entropy; null when it cannot (another user or
    /// PC, other entropy, damaged). DPAPI's own copy of the plaintext is wiped before it is freed; the caller
    /// should wipe the returned array once done with it.
    /// </summary>
    public static byte[]? Unprotect(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> entropy)
    {
        if (blob.IsEmpty) return null;
        fixed (byte* pBlob = blob)
        fixed (byte* pEntropy = entropy)
        {
            var input = new Blob(blob.Length, pBlob);
            var extra = new Blob(entropy.Length, pEntropy);
            if (!CryptUnprotectData(in input, IntPtr.Zero, in extra, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output))
                return null;
            var plain = new Span<byte>(output.Data, output.Size);
            try
            {
                return plain.ToArray();
            }
            finally
            {
                plain.Clear();
                LocalFree((IntPtr)output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Blob(int size, byte* data)
    {
        public readonly int Size = size;
        public readonly byte* Data = data;
    }

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(in Blob dataIn, IntPtr description, in Blob entropy, IntPtr reserved,
        IntPtr prompt, uint flags, out Blob dataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(in Blob dataIn, IntPtr description, in Blob entropy, IntPtr reserved,
        IntPtr prompt, uint flags, out Blob dataOut);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);
}
