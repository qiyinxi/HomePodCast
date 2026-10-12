using System.Security.Cryptography;

namespace HomePodCast.Protocol;

/// <summary>Ed25519 signatures (RFC 8032), which HAP pairing uses for the long-term identities; .NET has none.</summary>
public static class Ed25519
{
    public const int SeedSize = 32;
    public const int PublicKeySize = 32;
    public const int SignatureSize = 64;

    /// <summary>The public key of a 32-byte private key (RFC 8032's "secret key", the seed).</summary>
    public static byte[] PublicKey(byte[] seed)
    {
        CheckLength(seed, SeedSize, nameof(seed));
        return Curve25519.EdPublicKey(seed);
    }

    public static byte[] Sign(byte[] seed, byte[] message)
    {
        CheckLength(seed, SeedSize, nameof(seed));
        return Curve25519.EdSign(seed, message);
    }

    /// <summary>False for a wrong signature, a malformed key or signature, or a non-canonical S.</summary>
    public static bool Verify(byte[] publicKey, byte[] message, byte[] signature) =>
        publicKey.Length == PublicKeySize && signature.Length == SignatureSize && Curve25519.EdVerify(publicKey, message, signature);

    private static void CheckLength(byte[] value, int length, string name)
    {
        if (value.Length != length) throw new ArgumentException($"must be {length} bytes", name);
    }
}

/// <summary>X25519 Diffie-Hellman (RFC 7748), which HAP pair-verify uses for its session keys.</summary>
public static class X25519
{
    public const int KeySize = 32;

    private static readonly byte[] BasePoint = [9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>The function X25519(k, u) itself (32-byte little-endian scalar and u-coordinate).</summary>
    public static byte[] ScalarMult(byte[] scalar, byte[] u)
    {
        if (scalar.Length != KeySize || u.Length != KeySize) throw new ArgumentException($"X25519 takes {KeySize}-byte values");
        return Curve25519.ScalarMult(scalar, u);
    }

    public static byte[] PublicKey(byte[] privateKey) => ScalarMult(privateKey, BasePoint);

    /// <summary>The shared secret; throws for a peer key of small order (an all-zero result, RFC 7748 section 6.1).</summary>
    public static byte[] SharedSecret(byte[] privateKey, byte[] peerPublicKey)
    {
        var k = ScalarMult(privateKey, peerPublicKey);
        int any = 0;
        foreach (var b in k) any |= b;
        if (any == 0) throw new CryptographicException("X25519: the peer's public key has small order");
        return k;
    }
}

/// <summary>
/// The curve arithmetic behind <see cref="Ed25519"/> and <see cref="X25519"/>: a port of TweetNaCl's (by Daniel J. Bernstein,
/// Bernard van Gastel, Wesley Janssen, Tanja Lange, Peter Schwabe and Sjaak Smetsers; public domain,
/// https://tweetnacl.cr.yp.to/), kept close to the original so it can be compared line by line. Field elements are 16 limbs
/// of 16 bits in longs; secret-dependent choices are made with masks, not branches. It is slow by today's standards (a few
/// milliseconds per operation), which is nothing next to one handshake per connection. SHA-512 is .NET's. Checked against
/// the RFC 7748 and RFC 8032 test vectors in tests/HomePodCast.Tests/PairingTests.cs.
/// </summary>
internal static class Curve25519
{
    private static readonly long[] Gf0 = new long[16];
    private static readonly long[] Gf1 = Gf(1);
    private static readonly long[] Gf121665 = Gf(0xDB41, 1);

    // Edwards curve constants: d, 2d, the base point (X, Y) and sqrt(-1).
    private static readonly long[] D = Gf(0x78a3, 0x1359, 0x4dca, 0x75eb, 0xd8ab, 0x4141, 0x0a4d, 0x0070,
        0xe898, 0x7779, 0x4079, 0x8cc7, 0xfe73, 0x2b6f, 0x6cee, 0x5203);
    private static readonly long[] D2 = Gf(0xf159, 0x26b2, 0x9b94, 0xebd6, 0xb156, 0x8283, 0x149a, 0x00e0,
        0xd130, 0xeef3, 0x80f2, 0x198e, 0xfce7, 0x56df, 0xd9dc, 0x2406);
    private static readonly long[] X = Gf(0xd51a, 0x8f25, 0x2d60, 0xc956, 0xa7b2, 0x9525, 0xc760, 0x692c,
        0xdc5c, 0xfdd6, 0xe231, 0xc0a4, 0x53fe, 0xcd6e, 0x36d3, 0x2169);
    private static readonly long[] Y = Gf(0x6658, 0x6666, 0x6666, 0x6666, 0x6666, 0x6666, 0x6666, 0x6666,
        0x6666, 0x6666, 0x6666, 0x6666, 0x6666, 0x6666, 0x6666, 0x6666);
    private static readonly long[] I = Gf(0xa0b0, 0x4a0e, 0x1b27, 0xc4ee, 0xe478, 0xad2f, 0x1806, 0x2f43,
        0xd7a7, 0x3dfb, 0x0099, 0x2b4d, 0xdf0b, 0x4fc1, 0x2480, 0x2b83);

    /// <summary>The group order L, little-endian bytes.</summary>
    private static readonly long[] L =
    [
        0xed, 0xd3, 0xf5, 0x5c, 0x1a, 0x63, 0x12, 0x58, 0xd6, 0x9c, 0xf7, 0xa2, 0xde, 0xf9, 0xde, 0x14,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x10,
    ];

    private static long[] Gf(params long[] limbs)
    {
        var r = new long[16];
        limbs.CopyTo(r, 0);
        return r;
    }

    // ---------------------------------------------------------------- field arithmetic mod 2^255 - 19

    private static void Set(long[] r, long[] a) => Array.Copy(a, r, 16);

    private static void Carry(long[] o)
    {
        for (int i = 0; i < 16; i++)
        {
            o[i] += 1L << 16;
            long c = o[i] >> 16;
            if (i < 15) o[i + 1] += c - 1;
            else o[0] += 38 * (c - 1);
            o[i] -= c << 16;
        }
    }

    /// <summary>Swaps p and q when b is 1, leaves them when b is 0 (without a branch).</summary>
    private static void Select(long[] p, long[] q, long b)
    {
        long c = ~(b - 1);
        for (int i = 0; i < 16; i++)
        {
            long t = c & (p[i] ^ q[i]);
            p[i] ^= t;
            q[i] ^= t;
        }
    }

    private static void Pack(Span<byte> o, long[] n)
    {
        var m = new long[16];
        var t = new long[16];
        Set(t, n);
        Carry(t);
        Carry(t);
        Carry(t);
        for (int j = 0; j < 2; j++)
        {
            m[0] = t[0] - 0xffed;
            for (int i = 1; i < 15; i++)
            {
                m[i] = t[i] - 0xffff - ((m[i - 1] >> 16) & 1);
                m[i - 1] &= 0xffff;
            }
            m[15] = t[15] - 0x7fff - ((m[14] >> 16) & 1);
            long b = (m[15] >> 16) & 1;
            m[14] &= 0xffff;
            Select(t, m, 1 - b);
        }
        for (int i = 0; i < 16; i++)
        {
            o[2 * i] = (byte)(t[i] & 0xff);
            o[2 * i + 1] = (byte)(t[i] >> 8);
        }
    }

    private static bool Equal(long[] a, long[] b)
    {
        Span<byte> c = stackalloc byte[32];
        Span<byte> d = stackalloc byte[32];
        Pack(c, a);
        Pack(d, b);
        return CryptographicOperations.FixedTimeEquals(c, d);
    }

    private static int Parity(long[] a)
    {
        Span<byte> d = stackalloc byte[32];
        Pack(d, a);
        return d[0] & 1;
    }

    private static void Unpack(long[] o, ReadOnlySpan<byte> n)
    {
        for (int i = 0; i < 16; i++) o[i] = n[2 * i] + ((long)n[2 * i + 1] << 8);
        o[15] &= 0x7fff;
    }

    private static void Add(long[] o, long[] a, long[] b)
    {
        for (int i = 0; i < 16; i++) o[i] = a[i] + b[i];
    }

    private static void Sub(long[] o, long[] a, long[] b)
    {
        for (int i = 0; i < 16; i++) o[i] = a[i] - b[i];
    }

    private static void Mul(long[] o, long[] a, long[] b)
    {
        Span<long> t = stackalloc long[31];
        t.Clear();
        for (int i = 0; i < 16; i++)
            for (int j = 0; j < 16; j++)
                t[i + j] += a[i] * b[j];
        for (int i = 0; i < 15; i++) t[i] += 38 * t[i + 16];
        for (int i = 0; i < 16; i++) o[i] = t[i];
        Carry(o);
        Carry(o);
    }

    private static void Square(long[] o, long[] a) => Mul(o, a, a);

    private static void Inverse(long[] o, long[] i)
    {
        var c = new long[16];
        Set(c, i);
        for (int a = 253; a >= 0; a--)
        {
            Square(c, c);
            if (a != 2 && a != 4) Mul(c, c, i);
        }
        Set(o, c);
    }

    private static void Pow2523(long[] o, long[] i)
    {
        var c = new long[16];
        Set(c, i);
        for (int a = 250; a >= 0; a--)
        {
            Square(c, c);
            if (a != 1) Mul(c, c, i);
        }
        Set(o, c);
    }

    // ---------------------------------------------------------------- X25519 (Montgomery ladder)

    public static byte[] ScalarMult(byte[] n, byte[] p)
    {
        var z = new byte[32];
        Array.Copy(n, z, 31);
        z[31] = (byte)((n[31] & 127) | 64);
        z[0] &= 248;
        var x = new long[16];
        Unpack(x, p);
        long[] a = new long[16], b = new long[16], c = new long[16], d = new long[16], e = new long[16], f = new long[16];
        Set(b, x);
        a[0] = d[0] = 1;
        for (int i = 254; i >= 0; --i)
        {
            long r = (z[i >> 3] >> (i & 7)) & 1;
            Select(a, b, r);
            Select(c, d, r);
            Add(e, a, c);
            Sub(a, a, c);
            Add(c, b, d);
            Sub(b, b, d);
            Square(d, e);
            Square(f, a);
            Mul(a, c, a);
            Mul(c, b, e);
            Add(e, a, c);
            Sub(a, a, c);
            Square(b, a);
            Sub(c, d, f);
            Mul(a, c, Gf121665);
            Add(a, a, d);
            Mul(c, c, a);
            Mul(a, d, f);
            Mul(d, b, x);
            Square(b, e);
            Select(a, b, r);
            Select(c, d, r);
        }
        Inverse(c, c);
        Mul(a, a, c);
        var q = new byte[32];
        Pack(q, a);
        CryptographicOperations.ZeroMemory(z);
        return q;
    }

    // ---------------------------------------------------------------- Ed25519 (extended twisted Edwards coordinates)

    private static long[][] NewPoint() => [new long[16], new long[16], new long[16], new long[16]];

    private static void PointAdd(long[][] p, long[][] q)
    {
        long[] a = new long[16], b = new long[16], c = new long[16], d = new long[16], t = new long[16],
            e = new long[16], f = new long[16], g = new long[16], h = new long[16];
        Sub(a, p[1], p[0]);
        Sub(t, q[1], q[0]);
        Mul(a, a, t);
        Add(b, p[0], p[1]);
        Add(t, q[0], q[1]);
        Mul(b, b, t);
        Mul(c, p[3], q[3]);
        Mul(c, c, D2);
        Mul(d, p[2], q[2]);
        Add(d, d, d);
        Sub(e, b, a);
        Sub(f, d, c);
        Add(g, d, c);
        Add(h, b, a);
        Mul(p[0], e, f);
        Mul(p[1], h, g);
        Mul(p[2], g, f);
        Mul(p[3], e, h);
    }

    private static void CSwap(long[][] p, long[][] q, long b)
    {
        for (int i = 0; i < 4; i++) Select(p[i], q[i], b);
    }

    private static void PackPoint(Span<byte> r, long[][] p)
    {
        long[] tx = new long[16], ty = new long[16], zi = new long[16];
        Inverse(zi, p[2]);
        Mul(tx, p[0], zi);
        Mul(ty, p[1], zi);
        Pack(r, ty);
        r[31] ^= (byte)(Parity(tx) << 7);
    }

    /// <summary>p = s·q (q is used as scratch, as in TweetNaCl).</summary>
    private static void ScalarMultPoint(long[][] p, long[][] q, ReadOnlySpan<byte> s)
    {
        Set(p[0], Gf0);
        Set(p[1], Gf1);
        Set(p[2], Gf1);
        Set(p[3], Gf0);
        for (int i = 255; i >= 0; --i)
        {
            long b = (s[i / 8] >> (i & 7)) & 1;
            CSwap(p, q, b);
            PointAdd(q, p);
            PointAdd(p, p);
            CSwap(p, q, b);
        }
    }

    private static void ScalarBase(long[][] p, ReadOnlySpan<byte> s)
    {
        var q = NewPoint();
        Set(q[0], X);
        Set(q[1], Y);
        Set(q[2], Gf1);
        Mul(q[3], X, Y);
        ScalarMultPoint(p, q, s);
    }

    private static void ModL(Span<byte> r, long[] x)
    {
        long carry;
        int i, j;
        for (i = 63; i >= 32; --i)
        {
            carry = 0;
            for (j = i - 32; j < i - 12; ++j)
            {
                x[j] += carry - 16 * x[i] * L[j - (i - 32)];
                carry = (x[j] + 128) >> 8;
                x[j] -= carry << 8;
            }
            x[j] += carry;
            x[i] = 0;
        }
        carry = 0;
        for (j = 0; j < 32; j++)
        {
            x[j] += carry - (x[31] >> 4) * L[j];
            carry = x[j] >> 8;
            x[j] &= 255;
        }
        for (j = 0; j < 32; j++) x[j] -= carry * L[j];
        for (i = 0; i < 32; i++)
        {
            x[i + 1] += x[i] >> 8;
            r[i] = (byte)(x[i] & 255);
        }
    }

    /// <summary>A 64-byte hash reduced mod L, in place (the first 32 bytes; the rest become 0).</summary>
    private static void Reduce(byte[] r)
    {
        var x = new long[64];
        for (int i = 0; i < 64; i++) x[i] = r[i];
        Array.Clear(r);
        ModL(r, x);
    }

    /// <summary>SHA-512 of the seed, clamped: the first half is the secret scalar, the second the nonce prefix.</summary>
    private static byte[] ExpandSeed(byte[] seed)
    {
        var d = SHA512.HashData(seed);
        d[0] &= 248;
        d[31] &= 127;
        d[31] |= 64;
        return d;
    }

    public static byte[] EdPublicKey(byte[] seed)
    {
        var d = ExpandSeed(seed);
        var p = NewPoint();
        ScalarBase(p, d);
        var pk = new byte[32];
        PackPoint(pk, p);
        CryptographicOperations.ZeroMemory(d);
        return pk;
    }

    public static byte[] EdSign(byte[] seed, byte[] message)
    {
        var d = ExpandSeed(seed);
        var p = NewPoint();
        ScalarBase(p, d);
        var pk = new byte[32];
        PackPoint(pk, p);

        var r = SHA512.HashData(Concat(d.AsSpan(32, 32), message));
        Reduce(r);
        p = NewPoint();
        ScalarBase(p, r);
        var signature = new byte[64];
        PackPoint(signature, p);

        var h = SHA512.HashData(Concat(signature.AsSpan(0, 32), pk, message));
        Reduce(h);
        var x = new long[64];
        for (int i = 0; i < 32; i++) x[i] = r[i];
        for (int i = 0; i < 32; i++)
            for (int j = 0; j < 32; j++)
                x[i + j] += h[i] * (long)d[j];
        ModL(signature.AsSpan(32), x);
        CryptographicOperations.ZeroMemory(d);
        CryptographicOperations.ZeroMemory(r);
        return signature;
    }

    /// <summary>Decodes a public key as its negation (−A), as the verification needs it; false when it is no point.</summary>
    private static bool UnpackNeg(long[][] r, byte[] p)
    {
        long[] t = new long[16], chk = new long[16], num = new long[16], den = new long[16],
            den2 = new long[16], den4 = new long[16], den6 = new long[16];
        Set(r[2], Gf1);
        Unpack(r[1], p);
        Square(num, r[1]);
        Mul(den, num, D);
        Sub(num, num, r[2]);
        Add(den, r[2], den);

        Square(den2, den);
        Square(den4, den2);
        Mul(den6, den4, den2);
        Mul(t, den6, num);
        Mul(t, t, den);

        Pow2523(t, t);
        Mul(t, t, num);
        Mul(t, t, den);
        Mul(t, t, den);
        Mul(r[0], t, den);

        Square(chk, r[0]);
        Mul(chk, chk, den);
        if (!Equal(chk, num)) Mul(r[0], r[0], I);

        Square(chk, r[0]);
        Mul(chk, chk, den);
        if (!Equal(chk, num)) return false;

        if (Parity(r[0]) == (p[31] >> 7)) Sub(r[0], Gf0, r[0]);

        Mul(r[3], r[0], r[1]);
        return true;
    }

    /// <summary>S below the group order L (RFC 8032 section 5.1.7 rejects anything else).</summary>
    private static bool IsCanonicalScalar(ReadOnlySpan<byte> s)
    {
        for (int i = 31; i >= 0; i--)
        {
            if (s[i] < L[i]) return true;
            if (s[i] > L[i]) return false;
        }
        return false; // S == L
    }

    public static bool EdVerify(byte[] publicKey, byte[] message, byte[] signature)
    {
        if (!IsCanonicalScalar(signature.AsSpan(32, 32))) return false;
        var q = NewPoint();
        if (!UnpackNeg(q, publicKey)) return false;

        var h = SHA512.HashData(Concat(signature.AsSpan(0, 32), publicKey, message));
        Reduce(h);
        var p = NewPoint();
        ScalarMultPoint(p, q, h);    // h·(−A)
        ScalarBase(q, signature.AsSpan(32, 32));
        PointAdd(p, q);              // S·B − h·A, which must be R
        var t = new byte[32];
        PackPoint(t, p);
        return CryptographicOperations.FixedTimeEquals(signature.AsSpan(0, 32), t);
    }

    private static byte[] Concat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c = default)
    {
        var result = new byte[a.Length + b.Length + c.Length];
        a.CopyTo(result);
        b.CopyTo(result.AsSpan(a.Length));
        c.CopyTo(result.AsSpan(a.Length + b.Length));
        return result;
    }
}
