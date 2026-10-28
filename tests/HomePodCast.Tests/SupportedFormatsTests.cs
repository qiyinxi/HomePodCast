using HomePodCast.Net;
using HomePodCast.Protocol;

namespace HomePodCast.Tests;

/// <summary>/info's supportedFormats.bufferStream, the only proof that a HomePod decodes 5.1 (experimental since 1.4).</summary>
public sealed class SupportedFormatsTests
{
    private static Dictionary<string, object?> Info(object? bufferStream) => new()
    {
        ["supportedFormats"] = new Dictionary<string, object?> { ["bufferStream"] = bufferStream, ["lowLatencyAudioStream"] = 0L },
    };

    [Fact]
    public void Bit_39_is_read_however_the_plist_carries_the_mask()
    {
        Assert.True(SupportedFormats.Has(Info((1L << 39) | (1L << 22)), BufferedAudio.Surround51Bit));
        Assert.True(SupportedFormats.Has(Info(unchecked((long)(1UL << 63 | 1UL << 39))), 39)); // bit 63: a negative long
        Assert.True(SupportedFormats.Has(Info(1UL << 39), 39));
        Assert.Equal(1UL << 63 | 1UL << 39, SupportedFormats.BufferStream(Info(unchecked((long)(1UL << 63 | 1UL << 39)))));
        // As it comes over the wire.
        var wire = BPlist.ReadDict(BPlist.Write(Info(1UL << 63 | 1UL << 40 | 1UL << 39)));
        Assert.True(SupportedFormats.Has(wire, 39));
        Assert.Equal("bufferStream 0x8000018000000000 (5.1 bit 39 yes, 7.1 bit 40 yes)", SupportedFormats.Describe(wire));
    }

    [Fact]
    public void No_mask_or_no_bit_39_is_no_5_1()
    {
        Assert.False(SupportedFormats.Has(new Dictionary<string, object?>(), 39));
        Assert.False(SupportedFormats.Has(new Dictionary<string, object?> { ["supportedFormats"] = new Dictionary<string, object?>() }, 39));
        Assert.False(SupportedFormats.Has(new Dictionary<string, object?> { ["supportedFormats"] = "junk" }, 39));
        Assert.False(SupportedFormats.Has(Info("0x8000000000"), 39));
        Assert.False(SupportedFormats.Has(Info(1L << 40), 39)); // 7.1 only
        Assert.Null(SupportedFormats.BufferStream(new Dictionary<string, object?>()));
        Assert.Equal("no supportedFormats.bufferStream", SupportedFormats.Describe(new Dictionary<string, object?>()));
    }
}
