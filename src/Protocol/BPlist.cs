using System.Buffers.Binary;
using System.Text;

namespace HomePodCast.Protocol;

/// <summary>Minimal Apple binary property list (bplist00) reader/writer.</summary>
/// <remarks>
/// Reader produces: Dictionary&lt;string, object?&gt;, List&lt;object?&gt;, string, long, double, bool,
/// byte[], DateTime. Writer additionally accepts int/uint/ulong/float and object[].
/// </remarks>
public static class BPlist
{
    // ---------------------------------------------------------------- writer

    private enum Kind { Scalar, Array, Dict }

    private sealed record Entry(Kind Kind, object? Value, int[] Refs);

    public static byte[] Write(object root)
    {
        var entries = new List<Entry?>();
        Add(root, entries);
        int refSize = entries.Count < 256 ? 1 : 2;

        using var ms = new MemoryStream();
        ms.Write("bplist00"u8);
        var offsets = new long[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            offsets[i] = ms.Position;
            WriteEntry(ms, entries[i]!, refSize);
        }

        long tableOffset = ms.Position;
        int offsetSize = tableOffset < 256 ? 1 : tableOffset < 65536 ? 2 : 4;
        foreach (var off in offsets) WriteSized(ms, (ulong)off, offsetSize);

        Span<byte> trailer = stackalloc byte[32];
        trailer.Clear();
        trailer[6] = (byte)offsetSize;
        trailer[7] = (byte)refSize;
        BinaryPrimitives.WriteUInt64BigEndian(trailer[8..], (ulong)entries.Count);
        BinaryPrimitives.WriteUInt64BigEndian(trailer[16..], 0); // root is object 0
        BinaryPrimitives.WriteUInt64BigEndian(trailer[24..], (ulong)tableOffset);
        ms.Write(trailer);
        return ms.ToArray();
    }

    private static int Add(object value, List<Entry?> entries)
    {
        int idx = entries.Count;
        entries.Add(null);
        switch (Normalize(value))
        {
            case Dictionary<string, object?> dict:
                var keyRefs = dict.Keys.Select(k => Add(k, entries)).ToList();
                var valRefs = dict.Values.Select(v => Add(v ?? throw new ArgumentException("null in plist"), entries));
                entries[idx] = new Entry(Kind.Dict, null, [.. keyRefs, .. valRefs]);
                break;
            case List<object?> list:
                entries[idx] = new Entry(Kind.Array, null,
                    [.. list.Select(v => Add(v ?? throw new ArgumentException("null in plist"), entries))]);
                break;
            case var scalar:
                entries[idx] = new Entry(Kind.Scalar, scalar, []);
                break;
        }
        return idx;
    }

    private static object Normalize(object value) => value switch
    {
        int i => (long)i,
        uint u => (long)u,
        ulong ul => unchecked((long)ul),
        short s => (long)s,
        ushort us => (long)us,
        float f => (double)f,
        object?[] arr => arr.ToList(),
        _ => value,
    };

    private static void WriteEntry(Stream s, Entry e, int refSize)
    {
        switch (e.Kind)
        {
            case Kind.Array:
                WriteMarker(s, 0xA, e.Refs.Length);
                foreach (var r in e.Refs) WriteSized(s, (ulong)r, refSize);
                return;
            case Kind.Dict:
                WriteMarker(s, 0xD, e.Refs.Length / 2);
                foreach (var r in e.Refs) WriteSized(s, (ulong)r, refSize);
                return;
        }

        switch (e.Value)
        {
            case bool b:
                s.WriteByte(b ? (byte)0x09 : (byte)0x08);
                break;
            case long l:
                WriteInt(s, l);
                break;
            case double d:
                s.WriteByte(0x23);
                Span<byte> buf = stackalloc byte[8];
                BinaryPrimitives.WriteDoubleBigEndian(buf, d);
                s.Write(buf);
                break;
            case byte[] data:
                WriteMarker(s, 0x4, data.Length);
                s.Write(data);
                break;
            case string str when str.All(c => c < 128):
                WriteMarker(s, 0x5, str.Length);
                s.Write(Encoding.ASCII.GetBytes(str));
                break;
            case string str:
                WriteMarker(s, 0x6, str.Length);
                s.Write(Encoding.BigEndianUnicode.GetBytes(str));
                break;
            default:
                throw new NotSupportedException($"bplist: unsupported type {e.Value?.GetType()}");
        }
    }

    private static void WriteInt(Stream s, long v)
    {
        // 1/2/4-byte ints are unsigned in bplist; 8-byte ints are signed.
        if (v >= 0 && v < 0x100) { s.WriteByte(0x10); s.WriteByte((byte)v); }
        else if (v >= 0 && v < 0x10000) { s.WriteByte(0x11); WriteSized(s, (ulong)v, 2); }
        else if (v >= 0 && v < 0x100000000) { s.WriteByte(0x12); WriteSized(s, (ulong)v, 4); }
        else { s.WriteByte(0x13); WriteSized(s, unchecked((ulong)v), 8); }
    }

    private static void WriteMarker(Stream s, int type, int count)
    {
        if (count < 15) { s.WriteByte((byte)(type << 4 | count)); return; }
        s.WriteByte((byte)(type << 4 | 0xF));
        WriteInt(s, count);
    }

    private static void WriteSized(Stream s, ulong value, int size)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buf, value);
        s.Write(buf[(8 - size)..]);
    }

    // ---------------------------------------------------------------- reader

    public static object? Read(ReadOnlySpan<byte> data) => new Reader(data.ToArray()).ReadRoot();

    public static Dictionary<string, object?> ReadDict(ReadOnlySpan<byte> data) =>
        Read(data) as Dictionary<string, object?> ?? throw new InvalidDataException("bplist root is not a dict");

    private sealed class Reader(byte[] buf)
    {
        private int _offsetSize, _refSize;
        private long[] _offsets = [];

        public object? ReadRoot()
        {
            if (buf.Length < 40 || !buf.AsSpan(0, 8).SequenceEqual("bplist00"u8))
                throw new InvalidDataException("not a bplist00");
            var t = buf.AsSpan(buf.Length - 32);
            _offsetSize = t[6];
            _refSize = t[7];
            long count = (long)BinaryPrimitives.ReadUInt64BigEndian(t[8..]);
            long top = (long)BinaryPrimitives.ReadUInt64BigEndian(t[16..]);
            long table = (long)BinaryPrimitives.ReadUInt64BigEndian(t[24..]);
            _offsets = new long[count];
            for (long i = 0; i < count; i++)
                _offsets[i] = (long)ReadUInt(table + i * _offsetSize, _offsetSize);
            return ReadObject(top, 0);
        }

        private ulong ReadUInt(long pos, int size)
        {
            ulong v = 0;
            for (int i = 0; i < size; i++) v = v << 8 | buf[pos + i];
            return v;
        }

        private (int count, long start) ReadCount(long pos)
        {
            int low = buf[pos] & 0xF;
            if (low != 0xF) return (low, pos + 1);
            int intSize = 1 << (buf[pos + 1] & 0xF);
            return ((int)ReadUInt(pos + 2, intSize), pos + 2 + intSize);
        }

        private object? ReadObject(long objRef, int depth)
        {
            if (depth > 64) throw new InvalidDataException("bplist too deep");
            long pos = _offsets[objRef];
            byte marker = buf[pos];
            int type = marker >> 4;
            switch (type)
            {
                case 0x0:
                    return marker switch { 0x08 => false, 0x09 => true, _ => null };
                case 0x1:
                {
                    int size = 1 << (marker & 0xF);
                    if (size == 16) return (long)ReadUInt(pos + 9, 8); // 128-bit: take low 64
                    ulong raw = ReadUInt(pos + 1, size);
                    return size == 8 ? unchecked((long)raw) : (long)raw;
                }
                case 0x2:
                {
                    int size = 1 << (marker & 0xF);
                    return size == 4
                        ? BinaryPrimitives.ReadSingleBigEndian(buf.AsSpan((int)pos + 1))
                        : BinaryPrimitives.ReadDoubleBigEndian(buf.AsSpan((int)pos + 1));
                }
                case 0x3:
                    return new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                        .AddSeconds(BinaryPrimitives.ReadDoubleBigEndian(buf.AsSpan((int)pos + 1)));
                case 0x4:
                {
                    var (n, start) = ReadCount(pos);
                    return buf.AsSpan((int)start, n).ToArray();
                }
                case 0x5:
                {
                    var (n, start) = ReadCount(pos);
                    return Encoding.ASCII.GetString(buf, (int)start, n);
                }
                case 0x6:
                {
                    var (n, start) = ReadCount(pos);
                    return Encoding.BigEndianUnicode.GetString(buf, (int)start, n * 2);
                }
                case 0x8:
                    return (long)ReadUInt(pos + 1, (marker & 0xF) + 1);
                case 0xA:
                case 0xC:
                {
                    var (n, start) = ReadCount(pos);
                    var list = new List<object?>(n);
                    for (int i = 0; i < n; i++)
                        list.Add(ReadObject((long)ReadUInt(start + i * _refSize, _refSize), depth + 1));
                    return list;
                }
                case 0xD:
                {
                    var (n, start) = ReadCount(pos);
                    var dict = new Dictionary<string, object?>(n);
                    for (int i = 0; i < n; i++)
                    {
                        var key = ReadObject((long)ReadUInt(start + i * _refSize, _refSize), depth + 1) as string
                                  ?? throw new InvalidDataException("bplist dict key is not a string");
                        dict[key] = ReadObject((long)ReadUInt(start + (n + i) * _refSize, _refSize), depth + 1);
                    }
                    return dict;
                }
                default:
                    throw new InvalidDataException($"bplist: unsupported marker 0x{marker:X2}");
            }
        }
    }
}
