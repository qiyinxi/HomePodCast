using HomePodCast.Protocol;

namespace HomePodCast.Net;

/// <summary>
/// /info's "supportedFormats" dictionary: its "bufferStream" mask lists the buffered (type 103) formats a receiver
/// decodes, one bit per audioFormat bit. 2026-10-28: a HomePod answers 200 to a stream SETUP with any non-zero
/// audioFormat, so only this mask says whether it decodes 5.1 (bit 39, <see cref="BufferedAudio.Surround51Bit"/>).
/// </summary>
internal static class SupportedFormats
{
    /// <summary>The bufferStream mask; null when /info has none. A mask with bit 63 set comes out of the plist as a negative long.</summary>
    public static ulong? BufferStream(IReadOnlyDictionary<string, object?> info) =>
        info.GetValueOrDefault("supportedFormats") is IReadOnlyDictionary<string, object?> formats
            ? formats.GetValueOrDefault("bufferStream") switch
            {
                long l => unchecked((ulong)l),
                ulong u => u,
                int i => (uint)i,
                _ => null,
            }
            : null;

    public static bool Has(IReadOnlyDictionary<string, object?> info, int bit) => BufferStream(info) is { } mask && ((mask >> bit) & 1) != 0;

    /// <summary>For the log: "bufferStream 0x8000C00000 (5.1 bit 39 yes, 7.1 bit 40 no)".</summary>
    public static string Describe(IReadOnlyDictionary<string, object?> info) =>
        BufferStream(info) is { } mask
            ? $"bufferStream 0x{mask:X} (5.1 bit {BufferedAudio.Surround51Bit} {YesNo(mask, BufferedAudio.Surround51Bit)}, 7.1 bit 40 {YesNo(mask, 40)})"
            : "no supportedFormats.bufferStream";

    private static string YesNo(ulong mask, int bit) => ((mask >> bit) & 1) != 0 ? "yes" : "no";
}
