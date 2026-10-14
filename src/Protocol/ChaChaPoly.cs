using System.Buffers.Binary;
using System.Security.Cryptography;

namespace HomePodCast.Protocol;

/// <summary>
/// ChaCha20-Poly1305 (RFC 8439), which HAP pairing and the audio stream use: Windows' own where it has one, otherwise
/// this file's. 2026-10-14: a user on Windows 10 saw 「PlatformNotSupportedException: Algorithm 'ChaCha20Poly1305' is
/// not supported on this platform」 on every connection. .NET's ChaCha20Poly1305 needs Windows 10 build 20142 or later
/// (Windows 11), while HomePodCast supports Windows 10 2004 (19041).
/// </summary>
public sealed class ChaChaPoly : IDisposable
{
    public const int KeySize = 32, NonceSize = 12, TagSize = 16;

    private readonly ChaCha20Poly1305? _system;
    private readonly byte[]? _key;

    public ChaChaPoly(byte[] key) : this(key, managed: !ChaCha20Poly1305.IsSupported)
    {
    }

    /// <param name="managed">This file's implementation even where Windows has one (tests compare the two).</param>
    internal ChaChaPoly(byte[] key, bool managed)
    {
        if (key.Length != KeySize) throw new ArgumentException("ChaCha20-Poly1305 needs a 32-byte key", nameof(key));
        if (managed) _key = key.ToArray();
        else _system = new ChaCha20Poly1305(key);
    }

    /// <summary>Whether this copy uses its own implementation (no ChaCha20-Poly1305 in Windows): written to the log.</summary>
    public static bool UsesOwnImplementation => !ChaCha20Poly1305.IsSupported;

    public void Encrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, Span<byte> ciphertext, Span<byte> tag,
        ReadOnlySpan<byte> associatedData = default)
    {
        if (_system != null)
        {
            _system.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
            return;
        }
        Check(nonce, plaintext.Length, ciphertext.Length, tag.Length);
        Span<uint> state = stackalloc uint[16];
        Init(state, _key!, nonce);
        Span<byte> polyKey = stackalloc byte[64];
        Block(state, polyKey); // counter 0: the one-time Poly1305 key; the text starts at counter 1
        Xor(state, plaintext, ciphertext);
        Mac(polyKey[..32], associatedData, ciphertext, tag);
        CryptographicOperations.ZeroMemory(polyKey);
        state.Clear();
    }

    /// <summary>Throws <see cref="AuthenticationTagMismatchException"/> (a CryptographicException) when the tag doesn't match.</summary>
    public void Decrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag, Span<byte> plaintext,
        ReadOnlySpan<byte> associatedData = default)
    {
        if (_system != null)
        {
            _system.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
            return;
        }
        Check(nonce, plaintext.Length, ciphertext.Length, tag.Length);
        Span<uint> state = stackalloc uint[16];
        Init(state, _key!, nonce);
        Span<byte> polyKey = stackalloc byte[64];
        Block(state, polyKey);
        Span<byte> expected = stackalloc byte[TagSize];
        Mac(polyKey[..32], associatedData, ciphertext, expected);
        CryptographicOperations.ZeroMemory(polyKey);
        if (!CryptographicOperations.FixedTimeEquals(expected, tag))
        {
            plaintext[..ciphertext.Length].Clear();
            state.Clear();
            throw new AuthenticationTagMismatchException();
        }
        Xor(state, ciphertext, plaintext);
        state.Clear();
    }

    public void Dispose()
    {
        _system?.Dispose();
        if (_key != null) CryptographicOperations.ZeroMemory(_key);
    }

    private static void Check(ReadOnlySpan<byte> nonce, int plain, int cipher, int tag)
    {
        if (nonce.Length != NonceSize) throw new ArgumentException("ChaCha20-Poly1305 needs a 12-byte nonce", nameof(nonce));
        if (plain != cipher) throw new ArgumentException("plaintext and ciphertext must be the same length");
        if (tag != TagSize) throw new ArgumentException("ChaCha20-Poly1305 needs a 16-byte tag", nameof(tag));
    }

    // ---------------------------------------------------------------- ChaCha20 (RFC 8439 section 2.3)

    private static void Init(Span<uint> state, byte[] key, ReadOnlySpan<byte> nonce)
    {
        state[0] = 0x61707865; // "expand 32-byte k"
        state[1] = 0x3320646e;
        state[2] = 0x79622d32;
        state[3] = 0x6b206574;
        for (int i = 0; i < 8; i++) state[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key.AsSpan(4 * i));
        state[12] = 0; // block counter
        for (int i = 0; i < 3; i++) state[13 + i] = BinaryPrimitives.ReadUInt32LittleEndian(nonce[(4 * i)..]);
    }

    /// <summary>The key stream block for the state's counter, which then moves on by one.</summary>
    private static void Block(Span<uint> state, Span<byte> output)
    {
        Span<uint> x = stackalloc uint[16];
        state.CopyTo(x);
        for (int i = 0; i < 10; i++)
        {
            Quarter(x, 0, 4, 8, 12);
            Quarter(x, 1, 5, 9, 13);
            Quarter(x, 2, 6, 10, 14);
            Quarter(x, 3, 7, 11, 15);
            Quarter(x, 0, 5, 10, 15);
            Quarter(x, 1, 6, 11, 12);
            Quarter(x, 2, 7, 8, 13);
            Quarter(x, 3, 4, 9, 14);
        }
        for (int i = 0; i < 16; i++) BinaryPrimitives.WriteUInt32LittleEndian(output[(4 * i)..], x[i] + state[i]);
        x.Clear();
        state[12]++;
    }

    private static void Quarter(Span<uint> x, int a, int b, int c, int d)
    {
        x[a] += x[b]; x[d] = uint.RotateLeft(x[d] ^ x[a], 16);
        x[c] += x[d]; x[b] = uint.RotateLeft(x[b] ^ x[c], 12);
        x[a] += x[b]; x[d] = uint.RotateLeft(x[d] ^ x[a], 8);
        x[c] += x[d]; x[b] = uint.RotateLeft(x[b] ^ x[c], 7);
    }

    private static void Xor(Span<uint> state, ReadOnlySpan<byte> input, Span<byte> output)
    {
        Span<byte> stream = stackalloc byte[64];
        for (int at = 0; at < input.Length; at += 64)
        {
            Block(state, stream);
            int n = Math.Min(64, input.Length - at);
            for (int i = 0; i < n; i++) output[at + i] = (byte)(input[at + i] ^ stream[i]);
        }
        CryptographicOperations.ZeroMemory(stream);
    }

    // ---------------------------------------------------------------- Poly1305 (RFC 8439 sections 2.5 and 2.8)

    /// <summary>The AEAD tag: Poly1305 over aad, padding, ciphertext, padding and both lengths.</summary>
    private static void Mac(ReadOnlySpan<byte> key, ReadOnlySpan<byte> aad, ReadOnlySpan<byte> cipher, Span<byte> tag)
    {
        var poly = new Poly1305(key);
        poly.Update(aad, pad: true);
        poly.Update(cipher, pad: true);
        Span<byte> lengths = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(lengths, (ulong)aad.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(lengths[8..], (ulong)cipher.Length);
        poly.Update(lengths, pad: false);
        poly.Finish(tag);
    }

    /// <summary>Poly1305 in 26-bit limbs (the well-known "donna" 32-bit layout): h = (h + block) · r mod 2^130 − 5.</summary>
    internal struct Poly1305
    {
        private const uint Mask = 0x3ffffff;
        private readonly uint _r0, _r1, _r2, _r3, _r4, _s1, _s2, _s3, _s4;
        private readonly uint _pad0, _pad1, _pad2, _pad3;
        private uint _h0, _h1, _h2, _h3, _h4;

        public Poly1305(ReadOnlySpan<byte> key)
        {
            // r is clamped (RFC 8439 2.5.1) while it is split into limbs.
            _r0 = Le(key, 0) & 0x3ffffff;
            _r1 = (Le(key, 3) >> 2) & 0x3ffff03;
            _r2 = (Le(key, 6) >> 4) & 0x3ffc0ff;
            _r3 = (Le(key, 9) >> 6) & 0x3f03fff;
            _r4 = (Le(key, 12) >> 8) & 0x00fffff;
            _s1 = _r1 * 5; _s2 = _r2 * 5; _s3 = _r3 * 5; _s4 = _r4 * 5;
            _pad0 = Le(key, 16); _pad1 = Le(key, 20); _pad2 = Le(key, 24); _pad3 = Le(key, 28);
            _h0 = _h1 = _h2 = _h3 = _h4 = 0;
        }

        /// <param name="pad">An AEAD part: a last partial block is zero-padded to 16 bytes and counts as a full one.</param>
        public void Update(ReadOnlySpan<byte> data, bool pad)
        {
            Span<byte> last = stackalloc byte[17];
            while (data.Length >= 16)
            {
                Absorb(data[..16], 1 << 24);
                data = data[16..];
            }
            if (data.Length == 0) return;
            last.Clear();
            data.CopyTo(last);
            if (pad) Absorb(last[..16], 1 << 24);
            else
            {
                last[data.Length] = 1; // a short final block: 2^(8·length) is added inside the block itself
                Absorb(last[..16], 0);
            }
        }

        private void Absorb(ReadOnlySpan<byte> m, uint hibit)
        {
            uint h0 = _h0 + (Le(m, 0) & Mask);
            uint h1 = _h1 + ((Le(m, 3) >> 2) & Mask);
            uint h2 = _h2 + ((Le(m, 6) >> 4) & Mask);
            uint h3 = _h3 + ((Le(m, 9) >> 6) & Mask);
            uint h4 = _h4 + ((Le(m, 12) >> 8) | hibit);

            ulong d0 = (ulong)h0 * _r0 + (ulong)h1 * _s4 + (ulong)h2 * _s3 + (ulong)h3 * _s2 + (ulong)h4 * _s1;
            ulong d1 = (ulong)h0 * _r1 + (ulong)h1 * _r0 + (ulong)h2 * _s4 + (ulong)h3 * _s3 + (ulong)h4 * _s2;
            ulong d2 = (ulong)h0 * _r2 + (ulong)h1 * _r1 + (ulong)h2 * _r0 + (ulong)h3 * _s4 + (ulong)h4 * _s3;
            ulong d3 = (ulong)h0 * _r3 + (ulong)h1 * _r2 + (ulong)h2 * _r1 + (ulong)h3 * _r0 + (ulong)h4 * _s4;
            ulong d4 = (ulong)h0 * _r4 + (ulong)h1 * _r3 + (ulong)h2 * _r2 + (ulong)h3 * _r1 + (ulong)h4 * _r0;

            ulong c = d0 >> 26; h0 = (uint)d0 & Mask;
            d1 += c; c = d1 >> 26; h1 = (uint)d1 & Mask;
            d2 += c; c = d2 >> 26; h2 = (uint)d2 & Mask;
            d3 += c; c = d3 >> 26; h3 = (uint)d3 & Mask;
            d4 += c; c = d4 >> 26; h4 = (uint)d4 & Mask;
            h0 += (uint)c * 5; c = h0 >> 26; h0 &= Mask;
            h1 += (uint)c;

            _h0 = h0; _h1 = h1; _h2 = h2; _h3 = h3; _h4 = h4;
        }

        public void Finish(Span<byte> tag)
        {
            uint h0 = _h0, h1 = _h1, h2 = _h2, h3 = _h3, h4 = _h4;
            uint c = h1 >> 26; h1 &= Mask;
            h2 += c; c = h2 >> 26; h2 &= Mask;
            h3 += c; c = h3 >> 26; h3 &= Mask;
            h4 += c; c = h4 >> 26; h4 &= Mask;
            h0 += c * 5; c = h0 >> 26; h0 &= Mask;
            h1 += c;

            // h − p: kept if it doesn't go negative, i.e. h was ≥ p.
            uint g0 = h0 + 5; c = g0 >> 26; g0 &= Mask;
            uint g1 = h1 + c; c = g1 >> 26; g1 &= Mask;
            uint g2 = h2 + c; c = g2 >> 26; g2 &= Mask;
            uint g3 = h3 + c; c = g3 >> 26; g3 &= Mask;
            uint g4 = h4 + c - (1u << 26);
            uint select = (g4 >> 31) - 1; // all ones: take g
            h0 = (h0 & ~select) | (g0 & select);
            h1 = (h1 & ~select) | (g1 & select);
            h2 = (h2 & ~select) | (g2 & select);
            h3 = (h3 & ~select) | (g3 & select);
            h4 = (h4 & ~select) | (g4 & select);

            // To 4 × 32 bits, then + s mod 2^128.
            h0 = h0 | (h1 << 26);
            h1 = (h1 >> 6) | (h2 << 20);
            h2 = (h2 >> 12) | (h3 << 14);
            h3 = (h3 >> 18) | (h4 << 8);
            ulong f = (ulong)h0 + _pad0; h0 = (uint)f;
            f = (ulong)h1 + _pad1 + (f >> 32); h1 = (uint)f;
            f = (ulong)h2 + _pad2 + (f >> 32); h2 = (uint)f;
            f = (ulong)h3 + _pad3 + (f >> 32); h3 = (uint)f;
            BinaryPrimitives.WriteUInt32LittleEndian(tag, h0);
            BinaryPrimitives.WriteUInt32LittleEndian(tag[4..], h1);
            BinaryPrimitives.WriteUInt32LittleEndian(tag[8..], h2);
            BinaryPrimitives.WriteUInt32LittleEndian(tag[12..], h3);
        }

        private static uint Le(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b[at..]);
    }
}
