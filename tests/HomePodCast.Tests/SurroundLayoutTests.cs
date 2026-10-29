using System.Runtime.InteropServices;
using HomePodCast.Audio;

namespace HomePodCast.Tests;

/// <summary>Windows speaker layouts to the HomePod's 5.1 slots FL FR FC LFE Ls Rs (experimental since 1.4).</summary>
public sealed class SurroundLayoutTests
{
    private const float Split = 0.70710678f;

    private static float[] Convert(SurroundLayout layout, params float[] frame)
    {
        var output = new float[SurroundLayout.Slots];
        layout.Convert(frame, 1, output);
        return output;
    }

    private static SurroundLayout Accept(int channels, uint mask)
    {
        var layout = SurroundLayout.For(new WaveFormat(48000, channels, 32, channels * 4, true) { ChannelMask = mask }, out var refusal);
        Assert.Null(refusal);
        return Assert.IsType<SurroundLayout>(layout);
    }

    private static void Near(float[] expected, float[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], actual[i], 5);
    }

    [Fact]
    public void Back_5_1_is_the_encoders_order_unchanged()
    {
        var layout = Accept(6, 0x3F);
        Assert.Equal(new float[] { .1f, .2f, .3f, .4f, .5f, .6f }, Convert(layout, .1f, .2f, .3f, .4f, .5f, .6f));
        Assert.Equal("6 ch mask 0x3F → 5.1", layout.Describe);
    }

    [Fact]
    public void Side_5_1_goes_to_the_surround_pair()
    {
        // KSAUDIO_SPEAKER_5POINT1_SURROUND: FL FR FC LFE SL SR, what a 5.1 endpoint usually reports.
        var layout = Accept(6, 0x60F);
        Assert.Equal(new float[] { .1f, .2f, .3f, .4f, .5f, .6f }, Convert(layout, .1f, .2f, .3f, .4f, .5f, .6f));
        Assert.Equal("6 ch mask 0x60F → 5.1 (side→surround)", layout.Describe);
    }

    [Fact]
    public void Seven_point_one_sums_back_and_side_into_the_surround_pair()
    {
        // FL FR FC LFE BL BR SL SR
        var layout = Accept(8, 0x63F);
        Near([.1f, .2f, .3f, .4f, .2f + .3f, .1f + .4f], Convert(layout, .1f, .2f, .3f, .4f, .2f, .1f, .3f, .4f));
        Assert.Contains("7.1 folded", layout.Describe);
    }

    [Fact]
    public void A_5_1_film_on_a_7_1_endpoint_keeps_its_surround_level()
    {
        // Players put 5.1 on a 7.1 endpoint's side pair and leave the back pair silent: the same as on a 5.1 endpoint.
        var film = new float[] { .1f, .2f, .3f, .4f, .5f, .6f };
        var sevenOne = Convert(Accept(8, 0x63F), .1f, .2f, .3f, .4f, 0, 0, .5f, .6f);
        Assert.Equal(Convert(Accept(6, 0x60F), film), sevenOne);
        Assert.Equal(film, sevenOne);
    }

    [Fact]
    public void Front_centre_pair_is_split_between_the_front_pair_and_the_centre()
    {
        // FL FR FC LFE BL BR FLC FRC
        var layout = Accept(8, 0xFF);
        Near([.1f + Split *.7f, .2f + Split *.8f, .3f + Split *(.7f + .8f), .4f, .5f, .6f],
            Convert(layout, .1f, .2f, .3f, .4f, .5f, .6f, .7f, .8f));
        // The front pair itself stays at unity: dialogue and music keep their balance.
        Assert.Equal(new float[] { .5f, .5f, .5f, 0, 0, 0 }, Convert(layout, .5f, .5f, .5f, 0, 0, 0, 0, 0));
        Assert.Contains("front-centre pair→front+centre", layout.Describe);
    }

    [Fact]
    public void Back_centre_goes_to_both_surround_slots()
    {
        // FL FR FC LFE BC SL SR (6.1)
        var layout = Accept(7, 0x70F);
        Near([.1f, .2f, .3f, .4f, Split *.5f + .6f, Split *.5f + .7f], Convert(layout, .1f, .2f, .3f, .4f, .5f, .6f, .7f));
    }

    [Theory]
    [InlineData(6, 0u, 0x3Fu)]
    [InlineData(8, 0u, 0x63Fu)]
    [InlineData(6, 0x63Fu, 0x3Fu)] // a mask for another channel count is no guide either
    public void A_mask_that_does_not_fit_defaults_by_channel_count(int channels, uint mask, uint used)
    {
        var layout = Accept(channels, mask);
        Assert.Equal(used, layout.Mask);
        Assert.Contains($"mask taken as 0x{used:X}", layout.Describe);
    }

    [Theory]
    [InlineData(2, 0x3u)]
    [InlineData(2, 0u)]
    [InlineData(4, 0x33u)]      // quad
    [InlineData(5, 0x607u)]     // 5.0 side
    [InlineData(5, 0x37u)]      // 5.0 back
    [InlineData(6, 0x13Bu)]     // FL FR LFE BL BR BC: no centre
    public void Layouts_without_5_1_are_refused(int channels, uint mask)
    {
        var layout = SurroundLayout.For(new WaveFormat(48000, channels, 16, channels * 2, false) { ChannelMask = mask }, out var refusal);
        Assert.Null(layout);
        Assert.Equal(L.F("Windows 输出设备不是 5.1 或 7.1：现在是 {0} 声道", channels), refusal);
    }

    [Fact]
    public void LFE_stays_discrete()
    {
        foreach (var (channels, mask) in new[] { (6, 0x3Fu), (6, 0x60Fu), (8, 0x63Fu), (8, 0xFFu) })
        {
            var frame = new float[channels];
            frame[3] = .9f;
            Assert.Equal(new float[] { 0, 0, 0, .9f, 0, 0 }, Convert(Accept(channels, mask), frame));
        }
    }

    [Fact]
    public void Top_channels_are_dropped()
    {
        // 5.1.2: FL FR FC LFE SL SR TFL TFR
        var layout = Accept(8, 0x560F);
        Assert.Equal(new float[] { .1f, .2f, .3f, .4f, .5f, .6f }, Convert(layout, .1f, .2f, .3f, .4f, .5f, .6f, .7f, .8f));
        Assert.Contains("dropped 0x5000", layout.Describe);
    }

    [Theory]
    [InlineData(16, false)]
    [InlineData(24, false)]
    [InlineData(32, false)]
    [InlineData(32, true)]
    public void Every_sample_format_is_read(int bits, bool isFloat)
    {
        var fmt = new WaveFormat(48000, 6, bits, 6 * bits / 8, isFloat) { ChannelMask = 0x60F };
        var layout = SurroundLayout.For(fmt, out _)!;
        float[] values = [.5f, -.25f, .125f, -.5f, .75f, -.75f];
        var data = new byte[2 * fmt.BlockAlign];
        for (int f = 0; f < 2; f++)
            for (int c = 0; c < 6; c++)
            {
                var slot = data.AsSpan(f * fmt.BlockAlign + c * bits / 8, bits / 8);
                float v = f == 1 && isFloat && c == 2 ? float.NaN : values[c];
                switch (bits, isFloat)
                {
                    case (16, _): BitConverter.TryWriteBytes(slot, (short)(v * 32768)); break;
                    case (24, _): BitConverter.GetBytes((int)(v * 2147483648.0)).AsSpan(1, 3).CopyTo(slot); break;
                    case (32, false): BitConverter.TryWriteBytes(slot, (int)(v * 2147483648.0)); break;
                    default: BitConverter.TryWriteBytes(slot, v); break;
                }
            }
        var output = new float[2 * SurroundLayout.Slots];
        var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
        try { layout.Convert(pinned.AddrOfPinnedObject(), 2, fmt, output); }
        finally { pinned.Free(); }
        Near(values, output[..6]);
        if (isFloat) Assert.Equal(0f, output[6 + 2]); // a NaN from the mix becomes silence
        else Near(values, output[6..]);
    }
}
