using System.Buffers.Binary;

namespace HomePodCast.Net;

/// <summary>Classic RAOP packet layouts; verified against pyatv protocols/raop/packets.py and stream_client.py.</summary>
public static class RaopPackets
{
    public static byte[] Audio(ushort sequence, uint timestamp, uint ssrc, ReadOnlySpan<byte> payload, bool first = false)
    {
        var packet = new byte[12 + payload.Length];
        packet[0] = 0x80; packet[1] = first ? (byte)0xe0 : (byte)0x60;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), ssrc);
        payload.CopyTo(packet.AsSpan(12));
        return packet;
    }

    public static byte[] Sync(uint timestamp, uint latency, ulong ntp, bool first)
    {
        var packet = new byte[20];
        packet[0] = first ? (byte)0x90 : (byte)0x80; packet[1] = 0xd4;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 7);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), unchecked(timestamp - latency));
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(8), ntp);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16), timestamp);
        return packet;
    }

    public static byte[] Resend(ReadOnlySpan<byte> original)
    {
        if (original.Length < 12) throw new ArgumentException("RTP packet required", nameof(original));
        var packet = new byte[4 + original.Length];
        packet[0] = 0x80; packet[1] = 0xd6;
        original.Slice(2, 2).CopyTo(packet.AsSpan(2));
        original.CopyTo(packet.AsSpan(4));
        return packet;
    }
}
