using System.Buffers.Binary;

namespace HomePodCast.Net;

/// <summary>IEEE 1588 v2 wire messages for a two-step unicast master (experimental since 1.1).</summary>
public static class PtpPackets
{
    // Field offsets verified against linuxptp msg.h (Richard Cochran, GPL-2.0-or-later), 2026-10-14:
    // https://github.com/richardcochran/linuxptp/blob/master/msg.h
    public const int HeaderSize = 34;
    public const byte Sync = 0, DelayRequest = 1, FollowUp = 8, DelayResponse = 9, Announce = 11;

    public static bool Valid(ReadOnlySpan<byte> packet, byte domain)
    {
        if (packet.Length < HeaderSize || (packet[1] & 15) != 2 || packet[4] != domain) return false;
        int size = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        int minimum = (packet[0] & 15) switch { Sync or DelayRequest or FollowUp => 44, DelayResponse => 54, Announce => 64, _ => HeaderSize };
        return size >= minimum && size <= packet.Length;
    }

    public static byte[] Header(byte type, int size, ulong clockId, ushort sequence, byte domain, sbyte interval,
        bool twoStep = false)
    {
        var packet = new byte[size];
        packet[0] = type;
        packet[1] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), checked((ushort)size));
        packet[4] = domain;
        packet[6] = (byte)(4 | (twoStep ? 2 : 0)); // unicast, optionally two-step; arbitrary time scale
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(20), clockId);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(28), 1);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(30), sequence);
        packet[32] = type switch { Sync => 0, DelayRequest => 1, FollowUp => 2, DelayResponse => 3, _ => 5 };
        packet[33] = unchecked((byte)interval);
        return packet;
    }

    public static void WriteTimestamp(Span<byte> target, ulong nanoseconds)
    {
        ulong seconds = nanoseconds / 1_000_000_000;
        if (seconds >= 1UL << 48) throw new ArgumentOutOfRangeException(nameof(nanoseconds));
        BinaryPrimitives.WriteUInt16BigEndian(target, (ushort)(seconds >> 32));
        BinaryPrimitives.WriteUInt32BigEndian(target[2..], (uint)seconds);
        BinaryPrimitives.WriteUInt32BigEndian(target[6..], (uint)(nanoseconds % 1_000_000_000));
    }

    public static ulong ReadTimestamp(ReadOnlySpan<byte> source)
    {
        ulong seconds = ((ulong)BinaryPrimitives.ReadUInt16BigEndian(source) << 32) | BinaryPrimitives.ReadUInt32BigEndian(source[2..]);
        uint fraction = BinaryPrimitives.ReadUInt32BigEndian(source[6..]);
        if (fraction >= 1_000_000_000) throw new FormatException("PTP nanosecond fraction out of range");
        return checked(seconds * 1_000_000_000 + fraction);
    }

    public static byte[] SyncMessage(ulong id, ushort sequence, byte domain = 0) => Header(Sync, 44, id, sequence, domain, -3, twoStep: true);

    public static byte[] FollowUpMessage(ulong id, ushort sequence, ulong sentNanoseconds, byte domain = 0)
    {
        var packet = Header(FollowUp, 44, id, sequence, domain, -3);
        WriteTimestamp(packet.AsSpan(HeaderSize), sentNanoseconds);
        return packet;
    }

    public static byte[] AnnounceMessage(ulong id, ushort sequence, byte domain = 0, byte priority = 128)
    {
        var packet = Header(Announce, 64, id, sequence, domain, 0);
        packet[47] = priority;
        packet[48] = 248; // ordinary clock, not traceable to a primary time source
        packet[49] = 0xfe; // unknown accuracy
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(50), 0xffff);
        packet[52] = priority;
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(53), id);
        packet[63] = 0xa0; // internal oscillator
        return packet;
    }

    public static byte[] DelayResponseMessage(ulong id, ReadOnlySpan<byte> request, ulong receivedNanoseconds, byte domain = 0)
    {
        if (!Valid(request, domain) || (request[0] & 15) != DelayRequest) throw new ArgumentException("not a delay request", nameof(request));
        var response = Header(DelayResponse, 54, id, BinaryPrimitives.ReadUInt16BigEndian(request[30..]), domain, 0x7f);
        WriteTimestamp(response.AsSpan(HeaderSize), receivedNanoseconds);
        request.Slice(20, 10).CopyTo(response.AsSpan(44));
        request.Slice(8, 8).CopyTo(response.AsSpan(8)); // preserve the requesting path's scaled correction field
        return response;
    }

    /// <summary>BMCA comparison for ordinary grandmasters: quality/priority tuple, then grandmaster identity.</summary>
    public static int CompareGrandmasters(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (!Valid(left, left.Length > 4 ? left[4] : (byte)0) || (left[0] & 15) != Announce
            || !Valid(right, right.Length > 4 ? right[4] : (byte)0) || (right[0] & 15) != Announce)
            throw new ArgumentException("announce messages required");
        return left.Slice(47, 14).SequenceCompareTo(right.Slice(47, 14));
    }
}
