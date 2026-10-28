using System.Buffers.Binary;

namespace HomePodCast.Protocol;

/// <summary>Type 103 TCP frames, as consumed by shairport-sync 4.3.7 rtp_buffered_audio_processor.</summary>
internal static class BufferedAudio
{
    public const int FramesPerPacket = 1024;
    public const int SampleRate = 44100;

    /// <summary>bufferStream / audioFormat bit of AAC-LC 48 kHz 5.1, which a HomePod 2 on OS 27 lists (experimental since 1.4).</summary>
    public const int Surround51Bit = 39;

    /// <summary>
    /// Apple senders put the format's bit index in the top byte of every type 103 packet's SSRC (0x27000000 = bit 39, AAC
    /// 48 kHz 5.1); the HomePod 5.1 path does the same (research and field test 2026-10-28). The third-party path keeps
    /// sending the session id.
    /// </summary>
    public static uint FormatSsrc(int bit) => (uint)bit << 24;

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

    /// <param name="networkTimeFlags">
    /// Added only when set, so the third-party anchor stays exactly as it was. 2026-10-28: a HomePod answered 400 to every
    /// anchor without it, and 200 with networkTimeFlags 0 (as Apple senders send it).
    /// </param>
    public static Dictionary<string, object?> Anchor(ulong clock, ulong nanoseconds, uint timestamp, int? networkTimeFlags = null)
    {
        var anchor = new Dictionary<string, object?>
        {
            ["networkTimeTimelineID"] = clock,
            ["networkTimeSecs"] = nanoseconds / 1_000_000_000,
            ["networkTimeFrac"] = (ulong)(((UInt128)(nanoseconds % 1_000_000_000) << 64) / 1_000_000_000),
            ["rtpTime"] = timestamp,
            ["rate"] = 1,
        };
        if (networkTimeFlags is { } flags) anchor["networkTimeFlags"] = flags;
        return anchor;
    }

    /// <summary>ADTS is only for independent decoder checks; AirPlay carries raw AAC access units.</summary>
    public static byte[] Adts(ReadOnlySpan<byte> aac)
    {
        int size = aac.Length + 7;
        if (size > 8191) throw new ArgumentOutOfRangeException(nameof(aac));
        byte[] result = [0xff, 0xf1, 0x50, (byte)(0x80 | (size >> 11)), (byte)(size >> 3), (byte)((size << 5) | 0x1f), 0xfc, .. aac];
        return result;
    }
}
