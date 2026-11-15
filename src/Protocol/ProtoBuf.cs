using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace HomePodCast.Protocol;

/// <summary>
/// Protocol Buffers' wire format, written by hand: the MediaRemote messages an Apple TV takes over the remote-control
/// channel (<see cref="Net.MediaRemote"/>) are a handful of small ones, so there is no schema compiler in the build.
/// Fields are written in the order of the calls; callers keep them in ascending field number, as protobuf's own
/// serializers do.
/// </summary>
public sealed class ProtoWriter
{
    private readonly MemoryStream _out = new();

    public ProtoWriter Varint(int field, ulong value)
    {
        Tag(field, ProtoField.VarintType);
        WriteVarint(_out, value);
        return this;
    }

    public ProtoWriter Bool(int field, bool value) => Varint(field, value ? 1u : 0u);

    public ProtoWriter Float(int field, float value)
    {
        Tag(field, ProtoField.Fixed32Type);
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        _out.Write(bytes);
        return this;
    }

    public ProtoWriter Bytes(int field, ReadOnlySpan<byte> value)
    {
        Tag(field, ProtoField.LengthType);
        WriteVarint(_out, (ulong)value.Length);
        _out.Write(value);
        return this;
    }

    public ProtoWriter String(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));

    public ProtoWriter Message(int field, ProtoWriter inner) => Bytes(field, inner.ToArray());

    public byte[] ToArray() => _out.ToArray();

    private void Tag(int field, int wireType) => WriteVarint(_out, (ulong)field << 3 | (uint)wireType);

    public static void WriteVarint(Stream output, ulong value)
    {
        while (value >= 0x80)
        {
            output.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        output.WriteByte((byte)value);
    }

    /// <summary>The varint at the front of <paramref name="data"/> and how many bytes it took.</summary>
    public static (ulong Value, int Length) ReadVarint(ReadOnlySpan<byte> data)
    {
        ulong value = 0;
        for (int i = 0; i < data.Length && i < 10; i++)
        {
            value |= (ulong)(data[i] & 0x7F) << (7 * i);
            if ((data[i] & 0x80) == 0) return (value, i + 1);
        }
        throw new InvalidDataException("protobuf: a varint that does not end");
    }
}

/// <summary>One field as it is on the wire: a number (<see cref="Value"/>) or bytes (<see cref="Data"/>).</summary>
public readonly record struct ProtoField(int Number, int WireType, ulong Value, byte[]? Data)
{
    public const int VarintType = 0, Fixed64Type = 1, LengthType = 2, Fixed32Type = 5;

    public string Text => Encoding.UTF8.GetString(Data ?? []);
    public float Single => BitConverter.Int32BitsToSingle((int)(uint)Value);
}

/// <summary>A message read without its schema: the fields in wire order, looked up by number.</summary>
public sealed class ProtoMessage
{
    public static readonly ProtoMessage Empty = new([]);

    public IReadOnlyList<ProtoField> Fields { get; }

    private ProtoMessage(List<ProtoField> fields) => Fields = fields;

    public static ProtoMessage Parse(ReadOnlySpan<byte> data)
    {
        var fields = new List<ProtoField>();
        int at = 0;
        while (at < data.Length)
        {
            var (tag, tagLength) = ProtoWriter.ReadVarint(data[at..]);
            at += tagLength;
            int number = (int)(tag >> 3), wireType = (int)(tag & 7);
            if (number == 0) throw new InvalidDataException("protobuf: field number 0");
            switch (wireType)
            {
                case ProtoField.VarintType:
                    var (value, length) = ProtoWriter.ReadVarint(data[at..]);
                    at += length;
                    fields.Add(new(number, wireType, value, null));
                    break;
                case ProtoField.Fixed64Type:
                    if (data.Length - at < 8) throw new InvalidDataException("protobuf: a cut-off 64-bit field");
                    fields.Add(new(number, wireType, BinaryPrimitives.ReadUInt64LittleEndian(data[at..]), null));
                    at += 8;
                    break;
                case ProtoField.Fixed32Type:
                    if (data.Length - at < 4) throw new InvalidDataException("protobuf: a cut-off 32-bit field");
                    fields.Add(new(number, wireType, BinaryPrimitives.ReadUInt32LittleEndian(data[at..]), null));
                    at += 4;
                    break;
                case ProtoField.LengthType:
                    var (size, sizeLength) = ProtoWriter.ReadVarint(data[at..]);
                    at += sizeLength;
                    if (size > (ulong)(data.Length - at)) throw new InvalidDataException("protobuf: a field longer than its message");
                    fields.Add(new(number, wireType, 0, data.Slice(at, (int)size).ToArray()));
                    at += (int)size;
                    break;
                default:
                    throw new InvalidDataException($"protobuf: wire type {wireType}");
            }
        }
        return new ProtoMessage(fields);
    }

    public static bool TryParse(ReadOnlySpan<byte> data, out ProtoMessage message)
    {
        try
        {
            message = Parse(data);
            return true;
        }
        catch (InvalidDataException)
        {
            message = Empty;
            return false;
        }
    }

    private ProtoField? Last(int number, int wireType)
    {
        for (int i = Fields.Count - 1; i >= 0; i--)
            if (Fields[i].Number == number && Fields[i].WireType == wireType) return Fields[i];
        return null;
    }

    public ulong? Varint(int number) => Last(number, ProtoField.VarintType)?.Value;
    public bool Bool(int number) => Varint(number) is > 0;
    public float? Float(int number) => Last(number, ProtoField.Fixed32Type)?.Single;
    public string? String(int number) => Last(number, ProtoField.LengthType)?.Text;
    public byte[]? Bytes(int number) => Last(number, ProtoField.LengthType)?.Data;

    /// <summary>The nested message in a field; null when the field is missing or is not a message.</summary>
    public ProtoMessage? Message(int number) =>
        Bytes(number) is { } data && TryParse(data, out var message) ? message : null;

    public IEnumerable<ProtoMessage> Messages(int number) =>
        Fields.Where(f => f.Number == number && f.WireType == ProtoField.LengthType)
            .Select(f => TryParse(f.Data, out var message) ? message : Empty);

    public IEnumerable<string> Strings(int number) =>
        Fields.Where(f => f.Number == number && f.WireType == ProtoField.LengthType).Select(f => f.Text);

    /// <summary>
    /// The message on one line by field number, for the verbose log: <c>{1=15, 2="…", 20={2="书房"}}</c>. Without a
    /// schema a run of bytes is a guess: text when it is printable, else a nested message when it parses as one, else
    /// its length and first bytes (never a key whole).
    /// </summary>
    public string Describe(int depth = 0) =>
        "{" + string.Join(", ", Fields.Select(f => $"{f.Number}={Describe(f, depth)}")) + "}";

    private static string Describe(ProtoField field, int depth)
    {
        switch (field.WireType)
        {
            case ProtoField.VarintType:
                return field.Value.ToString(CultureInfo.InvariantCulture);
            case ProtoField.Fixed32Type:
                return field.Single.ToString("0.###", CultureInfo.InvariantCulture) + "f";
            case ProtoField.Fixed64Type:
                return BitConverter.UInt64BitsToDouble(field.Value).ToString("0.###", CultureInfo.InvariantCulture) + "d";
        }
        var data = field.Data ?? [];
        if (data.Length == 0) return "\"\"";
        if (PrintableText(data) is { } text) return "\"" + (text.Length > 120 ? text[..120] + "…" : text) + "\"";
        if (depth < 8 && TryParse(data, out var nested) && nested.Fields.Count > 0) return nested.Describe(depth + 1);
        return $"<{data.Length} bytes {Convert.ToHexString(data.AsSpan(0, Math.Min(8, data.Length)))}{(data.Length > 8 ? "…" : "")}>";
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    private static string? PrintableText(byte[] data)
    {
        try
        {
            string text = StrictUtf8.GetString(data);
            return text.Any(char.IsControl) ? null : text;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
