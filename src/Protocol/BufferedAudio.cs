using System.Buffers.Binary;

namespace HomePodCast.Protocol;

/// <summary>Type 103 TCP frames, as consumed by shairport-sync 4.3.7 rtp_buffered_audio_processor.</summary>
internal static class BufferedAudio
{
    public const int FramesPerPacket = 1024;
    public const int SampleRate = 44100;

    public static byte[] Packet(CounterCipher cipher, uint sequence, uint timestamp, uint ssrc, ReadOnlySpan<byte> aac)
    {
        if (aac.IsEmpty || aac.Length > ushort.MaxValue - 38) throw new ArgumentOutOfRangeException(nameof(aac));
        var wire = new byte[2 + 12 + aac.Length + CounterCipher.TagSize + 8];
        BinaryPrimitives.WriteUInt16BigEndian(wire, checked((ushort)wire.Length));
        var packet = wire.AsSpan(2);
        packet[0] = 0x80;
        packet[1] = (byte)(sequence >> 16); // buffered streams use a 24-bit sequence, not a payload type
        packet[2] = (byte)(sequence >> 8);
        packet[3] = (byte)sequence;
        BinaryPrimitives.WriteUInt32BigEndian(packet[4..], timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(packet[8..], ssrc);
        ulong nonce = cipher.Counter;
        cipher.Encrypt(aac, packet.Slice(4, 8), packet.Slice(12, aac.Length + CounterCipher.TagSize));
        BinaryPrimitives.WriteUInt64LittleEndian(packet[^8..], nonce);
        return wire;
    }

    public static Dictionary<string, object?> Anchor(ulong clock, ulong nanoseconds, uint timestamp) => new()
    {
        ["networkTimeTimelineID"] = clock,
        ["networkTimeSecs"] = nanoseconds / 1_000_000_000,
        ["networkTimeFrac"] = (ulong)(((UInt128)(nanoseconds % 1_000_000_000) << 64) / 1_000_000_000),
        ["rtpTime"] = timestamp,
        ["rate"] = 1,
    };

    /// <summary>ADTS is only for independent decoder checks; AirPlay carries raw AAC access units.</summary>
    public static byte[] Adts(ReadOnlySpan<byte> aac)
    {
        int size = aac.Length + 7;
        if (size > 8191) throw new ArgumentOutOfRangeException(nameof(aac));
        byte[] result = [0xff, 0xf1, 0x50, (byte)(0x80 | (size >> 11)), (byte)(size >> 3), (byte)((size << 5) | 0x1f), 0xfc, .. aac];
        return result;
    }
}
