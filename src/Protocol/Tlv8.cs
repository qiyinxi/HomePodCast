namespace HomePodCast.Protocol;

/// <summary>HomeKit TLV8 encoding (values over 255 bytes are split into fragments).</summary>
public static class Tlv8
{
    public const byte Method = 0x00;
    public const byte Identifier = 0x01;
    public const byte Salt = 0x02;
    public const byte PublicKey = 0x03;
    public const byte Proof = 0x04;
    public const byte EncryptedData = 0x05;
    public const byte State = 0x06;
    public const byte Error = 0x07;
    public const byte BackOff = 0x08;
    public const byte Signature = 0x0A;
    public const byte Flags = 0x13;

    public const byte FlagTransient = 0x10;

    public static byte[] Write(params (byte Type, byte[] Value)[] items)
    {
        using var ms = new MemoryStream();
        foreach (var (type, value) in items)
        {
            int pos = 0;
            do
            {
                int n = Math.Min(255, value.Length - pos);
                ms.WriteByte(type);
                ms.WriteByte((byte)n);
                ms.Write(value, pos, n);
                pos += n;
            } while (pos < value.Length);
        }
        return ms.ToArray();
    }

    public static Dictionary<byte, byte[]> Read(ReadOnlySpan<byte> data)
    {
        var result = new Dictionary<byte, byte[]>();
        int pos = 0;
        while (pos + 2 <= data.Length)
        {
            byte type = data[pos];
            int len = data[pos + 1];
            if (pos + 2 + len > data.Length) throw new InvalidDataException("truncated TLV8");
            var value = data.Slice(pos + 2, len).ToArray();
            result[type] = result.TryGetValue(type, out var prev) ? [.. prev, .. value] : value;
            pos += 2 + len;
        }
        return result;
    }
}
