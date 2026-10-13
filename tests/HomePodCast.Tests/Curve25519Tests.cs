using System.Security.Cryptography;
using HomePodCast.Protocol;

namespace HomePodCast.Tests;

/// <summary>X25519 against RFC 7748 (section 5.2's vectors and iterations, section 6.1's Diffie-Hellman).</summary>
public class X25519Tests
{
    private static byte[] H(string hex) => Convert.FromHexString(hex);

    [Theory]
    [InlineData("a546e36bf0527c9d3b16154b82465edd62144c0ac1fc5a18506a2244ba449ac4",
        "e6db6867583030db3594c1a424b15f7c726624ec26b3353b10a903a6d0ab1c4c",
        "c3da55379de9c6908e94ea4df28d084f32eccf03491c71f754b4075577a28552")]
    [InlineData("4b66e9d4d1b4673c5ad22691957d6af5c11b6421e0ea01d42ca4169e7918ba0d",
        "e5210f12786811d3f4b7959d0538ae2c31dbe7106fc03c3efc4cd549c715a493",
        "95cbde9476e8907d7aade45cb4b873f88b595a68799fa152e6f8f7647aac7957")]
    public void Matches_rfc7748_section_5_2(string scalar, string u, string expected) =>
        Assert.Equal(H(expected), X25519.ScalarMult(H(scalar), H(u)));

    [Fact]
    public void Matches_rfc7748_iterations_1_and_1000()
    {
        var k = new byte[32];
        k[0] = 9;
        var u = (byte[])k.Clone();
        for (int i = 1; i <= 1000; i++)
        {
            var result = X25519.ScalarMult(k, u);
            u = k;
            k = result;
            if (i == 1) Assert.Equal(H("422c8e7a6227d7bca1350b3e2bb7279f7897b87bb6854b783c60e80311ae3079"), k);
        }
        Assert.Equal(H("684cf59ba83309552800ef566f2f4d3c1c3887c49360e3875f2eb94d99532c51"), k);
    }

    [Fact]
    public void Diffie_hellman_matches_rfc7748_section_6_1()
    {
        var alice = H("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
        var bob = H("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb");
        var alicePublic = X25519.PublicKey(alice);
        var bobPublic = X25519.PublicKey(bob);
        Assert.Equal(H("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a"), alicePublic);
        Assert.Equal(H("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f"), bobPublic);
        var k = H("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742");
        Assert.Equal(k, X25519.SharedSecret(alice, bobPublic));
        Assert.Equal(k, X25519.SharedSecret(bob, alicePublic));
    }

    [Fact]
    public void A_small_order_peer_key_is_refused() =>
        Assert.Throws<CryptographicException>(() => X25519.SharedSecret(RandomNumberGenerator.GetBytes(32), new byte[32]));
}

/// <summary>Ed25519 against RFC 8032 section 7.1 (TEST 1, 2, 3 and SHA(abc)).</summary>
public class Ed25519Tests
{
    public static TheoryData<string, string, string, string> Vectors => new()
    {
        {
            "9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60",
            "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a",
            "",
            "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b"
        },
        {
            "4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb",
            "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c",
            "72",
            "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00"
        },
        {
            "c5aa8df43f9f837bedb7442f31dcb7b166d38535076f094b85ce3a2e0b4458f7",
            "fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025",
            "af82",
            "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a"
        },
        {
            "833fe62409237b9d62ec77587520911e9a759cec1d19755b7da901b96dca3d42",
            "ec172b93ad5e563bf4932c70e1245034c35467ef2efd4d64ebf819683467e2bf",
            "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f",
            "dc2a4459e7369633a52b1bf277839a00201009a3efbf3ecb69bea2186c26b58909351fc9ac90b3ecfdfbc7c66431e0303dca179c138ac17ad9bef1177331a704"
        },
    };

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Matches_rfc8032(string secret, string publicKey, string message, string signature)
    {
        var seed = Convert.FromHexString(secret);
        var msg = Convert.FromHexString(message);
        Assert.Equal(Convert.FromHexString(publicKey), Ed25519.PublicKey(seed));
        Assert.Equal(Convert.FromHexString(signature), Ed25519.Sign(seed, msg));
        Assert.True(Ed25519.Verify(Convert.FromHexString(publicKey), msg, Convert.FromHexString(signature)));
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Rejects_a_changed_message_signature_or_key(string secret, string publicKey, string message, string signature)
    {
        _ = secret;
        var pk = Convert.FromHexString(publicKey);
        var msg = Convert.FromHexString(message);
        var sig = Convert.FromHexString(signature);
        Assert.False(Ed25519.Verify(pk, [.. msg, 0x01], sig));
        for (int i = 0; i < 64; i += 21)
        {
            var bad = (byte[])sig.Clone();
            bad[i] ^= 0x04;
            Assert.False(Ed25519.Verify(pk, msg, bad));
        }
        var otherKey = (byte[])pk.Clone();
        otherKey[3] ^= 0x10;
        Assert.False(Ed25519.Verify(otherKey, msg, sig));
        Assert.False(Ed25519.Verify(pk, msg, sig[..63]));
    }

    [Fact]
    public void Rejects_a_non_canonical_s()
    {
        // TEST 1's signature with S + L in place of S: the same point equation, but RFC 8032 requires S < L.
        var sig = Convert.FromHexString("e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b");
        var s = new System.Numerics.BigInteger(sig.AsSpan(32), isUnsigned: true);
        var l = System.Numerics.BigInteger.Pow(2, 252) + System.Numerics.BigInteger.Parse("27742317777372353535851937790883648493");
        var bigger = (s + l).ToByteArray(isUnsigned: true);
        var malleable = new byte[64];
        sig.AsSpan(0, 32).CopyTo(malleable);
        bigger.CopyTo(malleable, 32);
        Assert.False(Ed25519.Verify(Convert.FromHexString("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a"), [], malleable));
    }

    [Fact]
    public void Signs_and_verifies_random_keys()
    {
        for (int i = 0; i < 4; i++)
        {
            var seed = RandomNumberGenerator.GetBytes(32);
            var msg = RandomNumberGenerator.GetBytes(10 + i * 100);
            Assert.True(Ed25519.Verify(Ed25519.PublicKey(seed), msg, Ed25519.Sign(seed, msg)));
        }
    }
}
