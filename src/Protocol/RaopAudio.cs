using System.Buffers.Binary;
using System.Security.Cryptography;

namespace HomePodCast.Protocol;

/// <summary>Classic RAOP audio: stereo 16-bit ALAC escape frames, with optional packet-local AES-CBC.</summary>
public static class RaopAudio
{
    public const int SampleRate = 44100, FramesPerPacket = 352;

    /// <summary>Original implementation of Apple's documented ALAC escape format (no predictive encoder).</summary>
    public static byte[] Alac(ReadOnlySpan<short> pcm)
    {
        // Layout checked against Apple's Apache-2.0 ALACEncoder::EncodeStereoEscape, 2026-10-14:
        // https://github.com/macosforge/alac/blob/master/codec/ALACEncoder.cpp
        if (pcm.Length != FramesPerPacket * 2) throw new ArgumentException("352 stereo frames required", nameof(pcm));
        var output = new byte[(23 + pcm.Length * 16 + 3 + 7) / 8];
        int bit = 0;
        void Write(uint value, int count)
        {
            for (int i = count - 1; i >= 0; i--, bit++) output[bit / 8] |= (byte)(((value >> i) & 1) << (7 - bit % 8));
        }
        Write(1, 3); // channel pair
        Write(0, 4); // element instance
        Write(0, 12); // reserved
        Write(1, 4); // full frame, no shifted bytes, uncompressed escape
        foreach (short sample in pcm) Write(unchecked((ushort)sample), 16);
        Write(7, 3); // end element; remaining bits are zero padding
        return output;
    }

    public static byte[] L16(ReadOnlySpan<short> pcm)
    {
        var output = new byte[pcm.Length * 2];
        for (int i = 0; i < pcm.Length; i++) BinaryPrimitives.WriteInt16BigEndian(output.AsSpan(i * 2), pcm[i]);
        return output;
    }

    /// <summary>Reset the IV for each packet; encrypt complete blocks, leave the tail unchanged.</summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> audio, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        var output = audio.ToArray();
        int blocks = audio.Length / 16 * 16;
        if (blocks > 0) aes.EncryptCbc(audio[..blocks], iv, output.AsSpan(0, blocks), PaddingMode.None);
        return output;
    }

    // AirPort Express public modulus, also published by OwnTone src/outputs/raop.c (GPL-2.0-or-later).
    // Public protocol key data; no private key or receiver credential is included.
    public static RSA AirportPublicKey()
    {
        var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters
        {
            Exponent = [1, 0, 1],
            Modulus = Convert.FromHexString(
                "e7d744f2a2e2788b6c1f55a08eb70544a8fa7945aa8be6c62ce5f51cbdd4dc68" +
                "42fe3d1083dd2edec1bfd4252dc02e6f398bdf0e6148ea84855e2e442da6d626" +
                "64f674a1f304929ade4f6893ef2df6e711a8c77a0d91c9d980822e50d12922af" +
                "ea40ea9f0e14c0f76938c5f3882fc0323dd9fe55155f51bb5921c201629fd733" +
                "52d5e2efaabf9ba048d7b813a2b6767f6c3ccf1eb4ce673d037b0d2ea30c5fff" +
                "eb06f8d08adde409571a9c689fef10728855dd8cfb9a8bef5c8943ef3b5faa15" +
                "dde698beddf3599603eb3e6f61372bb628f6559f599a78bf500687aa7f4976c0" +
                "562d412956f8989e18a6355bd81597825e0fc875343ec782117625cdbf98447b"),
        });
        return rsa;
    }
}
