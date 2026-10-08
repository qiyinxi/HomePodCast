using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace HomePodCast.Protocol;

/// <summary>
/// SRP-6a client as used by HomeKit / AirPlay 2 pair-setup (3072-bit group, SHA-512).
/// Byte encodings deliberately match srptools (used by pyatv), which is known to pair with HomePods:
/// integers are hashed in their minimal big-endian form except where PAD() is noted.
/// </summary>
public sealed class SrpClient
{
    private static readonly BigInteger N = Parse(
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA6" +
        "3B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245" +
        "E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7EDEE386BFB5A899FA5AE9F2411" +
        "7C4B1FE649286651ECE45B3DC2007CB8A163BF0598DA48361C55D39A69163FA8FD24CF5F" +
        "83655D23DCA3AD961C62F356208552BB9ED529077096966D670C354E4ABC9804F1746C08" +
        "CA18217C32905E462E36CE3BE39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9" +
        "DE2BCBF6955817183995497CEA956AE515D2261898FA051015728E5A8AAAC42DAD33170D" +
        "04507A33A85521ABDF1CBA64ECFB850458DBEF0A8AEA71575D060C7DB3970F85A6E1E4C7" +
        "ABF5AE8CDB0933D71E8C94E04A25619DCEE3D2261AD2EE6BF12FFA06D98A0864D8760273" +
        "3EC86A64521F2B18177B200CBBE117577A615D6C770988C0BAD946E208E24FA074E5AB31" +
        "43DB5BFCE0FD108E4B82D120A93AD2CAFFFFFFFFFFFFFFFF");

    private static readonly BigInteger G = 5;
    private static readonly int PadLength = Bytes(N).Length;

    private readonly string _username;
    private readonly string _password;
    private readonly BigInteger _a;

    public byte[] PublicKey { get; }
    public byte[]? SessionKey { get; private set; }

    public SrpClient(string username, string password) : this(username, password, RandomNumberGenerator.GetBytes(32))
    {
    }

    /// <summary>Fixed private key — for reproducible test vectors only.</summary>
    internal SrpClient(string username, string password, byte[] privateKey)
    {
        _username = username;
        _password = password;
        _a = new BigInteger(privateKey, isUnsigned: true, isBigEndian: true);
        PublicKey = Bytes(BigInteger.ModPow(G, _a, N));
    }

    /// <summary>Process the server's salt and public key B; returns the client proof M1.</summary>
    public byte[] ComputeProof(byte[] salt, byte[] serverPublic)
    {
        var B = new BigInteger(serverPublic, isUnsigned: true, isBigEndian: true);
        if (B % N == 0) throw new CryptographicException("invalid SRP server public key");
        var A = new BigInteger(PublicKey, isUnsigned: true, isBigEndian: true);

        var k = HashInt(Bytes(N), Pad(G));                                         // k = H(N | PAD(g))
        var u = HashInt(Pad(A), Pad(B));                                           // u = H(PAD(A) | PAD(B))
        var inner = SHA512.HashData(Encoding.UTF8.GetBytes($"{_username}:{_password}"));
        var x = HashInt(salt, inner);                                              // x = H(s | H(I:P))
        var v = BigInteger.ModPow(G, x, N);

        var baseValue = ((B - k * v) % N + N) % N;                                 // keep non-negative
        var S = BigInteger.ModPow(baseValue, _a + u * x, N);
        SessionKey = SHA512.HashData(Bytes(S));                                    // K = H(S)

        var hNxorG = HashInt(Bytes(N)) ^ HashInt(Bytes(G));
        var hUser = HashInt(Encoding.UTF8.GetBytes(_username));
        return SHA512.HashData(Concat(Bytes(hNxorG), Bytes(hUser), salt, Bytes(A), Bytes(B), SessionKey));
    }

    private static BigInteger Parse(string hex) =>
        new(Convert.FromHexString(hex), isUnsigned: true, isBigEndian: true);

    private static byte[] Bytes(BigInteger v) => v.ToByteArray(isUnsigned: true, isBigEndian: true);

    private static byte[] Pad(BigInteger v)
    {
        var b = Bytes(v);
        if (b.Length >= PadLength) return b;
        var padded = new byte[PadLength];
        b.CopyTo(padded, PadLength - b.Length);
        return padded;
    }

    private static BigInteger HashInt(params byte[][] parts) =>
        new(SHA512.HashData(Concat(parts)), isUnsigned: true, isBigEndian: true);

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        int pos = 0;
        foreach (var p in parts) { p.CopyTo(result, pos); pos += p.Length; }
        return result;
    }
}
