using System.Security.Cryptography;
using System.Text;
using HomePodCast.Protocol;

namespace HomePodCast.Tests;

/// <summary>
/// Our own ChaCha20-Poly1305, used where Windows has none (Windows 10: 2026-10-14, a user could not connect at all):
/// RFC 8439's test vectors, and byte for byte the same as Windows' own where both exist.
/// </summary>
public class ChaChaPolyTests
{
    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", "").Replace(":", ""));

    [Fact]
    public void RFC_8439_AEAD_vector()
    {
        var key = Enumerable.Range(0x80, 32).Select(i => (byte)i).ToArray();
        var nonce = Hex("07000000 4041424344454647");
        var aad = Hex("50515253c0c1c2c3c4c5c6c7");
        var plain = Encoding.ASCII.GetBytes("Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it.");
        var expected = Hex("d31a8d34648e60db7b86afbc53ef7ec2a4aded51296e08fea9e2b5a736ee62d63dbea45e8ca9671282fafb69da92728b" +
                           "1a71de0a9e060b2905d6a5b67ecd3b3692ddbd7f2d778b8c9803aee328091b58fab324e4fad675945585808b4831d7bc" +
                           "3ff4def08e4b7a9de576d26586cec64b6116");
        var expectedTag = Hex("1ae10b594f09e26a7e902ecbd0600691");

        using var aead = new ChaChaPoly(key, managed: true);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        aead.Encrypt(nonce, plain, cipher, tag, aad);
        Assert.Equal(expected, cipher);
        Assert.Equal(expectedTag, tag);

        var back = new byte[cipher.Length];
        aead.Decrypt(nonce, cipher, tag, back, aad);
        Assert.Equal(plain, back);
    }

    [Fact]
    public void RFC_8439_Poly1305_vector()
    {
        var key = Hex("85:d6:be:78:57:55:6d:33:7f:44:52:fe:42:d5:06:a8:01:03:80:8a:fb:0d:b2:fd:4a:bf:f6:af:41:49:f5:1b");
        var poly = new ChaChaPoly.Poly1305(key);
        poly.Update(Encoding.ASCII.GetBytes("Cryptographic Forum Research Group"), pad: false);
        var tag = new byte[16];
        poly.Finish(tag);
        Assert.Equal(Hex("a8:06:1d:c1:30:51:36:c6:c2:2b:8b:af:0c:01:27:a9"), tag);
    }

    [Fact]
    public void Same_bytes_as_Windows_for_every_length_around_the_block_sizes()
    {
        if (!ChaCha20Poly1305.IsSupported) return; // Windows 10: only the RFC vectors above
        var random = new Random(8439);
        foreach (int length in Enumerable.Range(0, 200).Concat([255, 256, 257, 1023, 1024, 1408, 1500]))
        {
            var key = new byte[32]; random.NextBytes(key);
            var nonce = new byte[12]; random.NextBytes(nonce);
            var aad = new byte[random.Next(0, 40)]; random.NextBytes(aad);
            var plain = new byte[length]; random.NextBytes(plain);

            using var ours = new ChaChaPoly(key, managed: true);
            using var windows = new ChaCha20Poly1305(key);
            byte[] c1 = new byte[length], t1 = new byte[16], c2 = new byte[length], t2 = new byte[16];
            ours.Encrypt(nonce, plain, c1, t1, aad);
            windows.Encrypt(nonce, plain, c2, t2, aad);
            Assert.Equal(c2, c1);
            Assert.Equal(t2, t1);

            var back = new byte[length];
            ours.Decrypt(nonce, c2, t2, back, aad);
            Assert.Equal(plain, back);
        }
    }

    [Fact]
    public void A_changed_byte_fails_and_leaves_no_plaintext()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.ASCII.GetBytes("pair-verify M3: the controller's signature");
        using var aead = new ChaChaPoly(key, managed: true);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        aead.Encrypt(nonce, plain, cipher, tag);

        cipher[5] ^= 1;
        var output = Enumerable.Repeat((byte)0xAA, plain.Length).ToArray();
        Assert.Throws<AuthenticationTagMismatchException>(() => aead.Decrypt(nonce, cipher, tag, output));
        Assert.All(output, b => Assert.Equal(0, b));
        cipher[5] ^= 1;
        tag[15] ^= 0x80;
        Assert.ThrowsAny<CryptographicException>(() => aead.Decrypt(nonce, cipher, tag, output));
    }
}
