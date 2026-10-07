using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace HomePodCast.Protocol;

public static class HapKeys
{
    public static byte[] Derive(byte[] sharedSecret, string salt, string info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA512, sharedSecret, 32,
            Encoding.UTF8.GetBytes(salt), Encoding.UTF8.GetBytes(info));
}

/// <summary>ChaCha20-Poly1305 with a 64-bit little-endian counter nonce (4 zero bytes + counter).</summary>
public sealed class CounterCipher : IDisposable
{
    public const int TagSize = 16;
    private readonly ChaCha20Poly1305 _aead;
    private ulong _counter;

    public CounterCipher(byte[] key) => _aead = new ChaCha20Poly1305(key);

    public ulong Counter => _counter;

    /// <summary>Encrypt with the next counter value; output = ciphertext || tag.</summary>
    public void Encrypt(ReadOnlySpan<byte> plain, ReadOnlySpan<byte> aad, Span<byte> output)
    {
        Span<byte> nonce = stackalloc byte[12];
        MakeNonce(nonce, _counter++);
        _aead.Encrypt(nonce, plain, output[..plain.Length], output.Slice(plain.Length, TagSize), aad);
    }

    public void Decrypt(ReadOnlySpan<byte> cipherAndTag, ReadOnlySpan<byte> aad, Span<byte> output)
    {
        Span<byte> nonce = stackalloc byte[12];
        MakeNonce(nonce, _counter++);
        int n = cipherAndTag.Length - TagSize;
        _aead.Decrypt(nonce, cipherAndTag[..n], cipherAndTag[n..], output[..n], aad);
    }

    public static void MakeNonce(Span<byte> nonce, ulong counter)
    {
        nonce[..4].Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], counter);
    }

    public void Dispose() => _aead.Dispose();
}

/// <summary>
/// HAP session framing: plaintext is cut into ≤1024-byte frames, each sent as
/// [len:u16 LE][ciphertext][tag] with the length bytes as AAD.
/// </summary>
public sealed class HapSession : IDisposable
{
    private const int FrameLength = 1024;
    private readonly CounterCipher _out;
    private readonly CounterCipher _in;
    private readonly List<byte> _pending = new();

    public HapSession(byte[] outputKey, byte[] inputKey)
    {
        _out = new CounterCipher(outputKey);
        _in = new CounterCipher(inputKey);
    }

    public byte[] Encrypt(ReadOnlySpan<byte> data)
    {
        int frames = (data.Length + FrameLength - 1) / FrameLength;
        var output = new byte[data.Length + frames * (2 + CounterCipher.TagSize)];
        int src = 0, dst = 0;
        while (src < data.Length)
        {
            int n = Math.Min(FrameLength, data.Length - src);
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(dst), (ushort)n);
            _out.Encrypt(data.Slice(src, n), output.AsSpan(dst, 2), output.AsSpan(dst + 2, n + CounterCipher.TagSize));
            src += n;
            dst += 2 + n + CounterCipher.TagSize;
        }
        return output;
    }

    /// <summary>Feed received bytes; returns whatever complete frames decrypt to (may be empty).</summary>
    public byte[] Decrypt(ReadOnlySpan<byte> data)
    {
        _pending.AddRange(data);
        using var plain = new MemoryStream();
        while (_pending.Count >= 2)
        {
            int n = _pending[0] | _pending[1] << 8;
            int total = 2 + n + CounterCipher.TagSize;
            if (_pending.Count < total) break;
            var frame = _pending.GetRange(0, total).ToArray();
            var outBuf = new byte[n];
            _in.Decrypt(frame.AsSpan(2), frame.AsSpan(0, 2), outBuf);
            plain.Write(outBuf);
            _pending.RemoveRange(0, total);
        }
        return plain.ToArray();
    }

    public void Dispose()
    {
        _out.Dispose();
        _in.Dispose();
    }
}
