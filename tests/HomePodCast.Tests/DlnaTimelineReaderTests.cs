using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class DlnaTimelineReaderTests
{
    [Theory]
    [InlineData(-10)]
    [InlineData(10)]
    public void Initial_presentation_anchor_includes_receiver_latency_group_delay_and_signed_trim(int trim)
    {
        var timeline = new TimelineBuffer(44100, 10000, 123456);
        timeline.WriteFrames(0, Signal(0, 10000));
        var reader = new DlnaTimelineReader(timeline, 2025, trim, 2000, ChannelMode.Stereo);
        var pcm = new short[704];
        Assert.True(reader.Read(timeline.OriginTicks + MediaClock.FromMs(100), 1, pcm));
        long first = 4410 - (long)Math.Round((25 + trim) * 44.1);
        AssertFrames(first, pcm);
        Assert.Equal(first + 352, reader.Position);
    }

    [Fact]
    public void A_thousand_irregular_chunks_keep_sample_identity_and_never_reanchor_to_jittery_send_deadlines()
    {
        const int packets = 1000;
        var timeline = new TimelineBuffer(44100, packets * 352 + 8, 456789);
        long deadline = timeline.TicksAt(10007);
        long first = timeline.FrameAt(deadline + MediaClock.FromMs(2000)) - 89302;
        timeline.WriteFrames(first, Signal(first, packets * 352));
        var full = new DlnaTimelineReader(timeline, 2025, 0, 2000, ChannelMode.Stereo);
        var split = new DlnaTimelineReader(timeline, 2025, 0, 2000, ChannelMode.Stereo);
        var pcm = new short[704];
        var parts = new short[704];
        for (int packet = 0; packet < packets; packet++)
        {
            long now = packet == 0 ? deadline : deadline + MediaClock.FromMs(packet * 8 + packet % 19 * 3);
            Assert.True(full.Read(now, 1, pcm));
            Assert.True(split.Read(now, 1, parts.AsSpan(0, 254)));
            Assert.True(split.Read(now + MediaClock.FromMs(500), 1, parts.AsSpan(254)));
            AssertFrames(first + packet * 352L, pcm);
            Assert.Equal(pcm, parts);
        }
    }

    [Theory]
    [InlineData(-1000)]
    [InlineData(1000)]
    public void Fractional_resampling_moves_a_source_impulse_to_its_expected_output_frame_without_filter_delay(int ppm)
    {
        var timeline = new TimelineBuffer(44100, 10000, 0);
        var signal = new float[20000];
        signal[4000 * 2] = 1;
        signal[4000 * 2 + 1] = -1;
        timeline.WriteFrames(0, signal);
        var reader = new DlnaTimelineReader(timeline, 0, 0, 0, ChannelMode.Stereo);
        double ratio = 1 / (1 + ppm * 1e-6);
        var pcm = new short[704];
        int peak = -1, maximum = 0;
        for (int block = 0; block < 16; block++)
        {
            Assert.True(reader.Read(timeline.TicksAt(100), ratio, pcm));
            for (int i = 0; i < 352; i++)
            {
                Assert.Equal(-pcm[i * 2], pcm[i * 2 + 1]);
                if (Math.Abs((int)pcm[i * 2]) <= maximum) continue;
                maximum = Math.Abs((int)pcm[i * 2]);
                peak = block * 352 + i;
            }
        }
        Assert.InRange(peak, (int)Math.Round(3900 / ratio) - 1, (int)Math.Round(3900 / ratio) + 1);
        Assert.InRange(maximum, 22000, 32767);
        Assert.InRange(Math.Abs(reader.Position - (100 + 16 * 352 * ratio)), 0, 1e-8);
    }

    [Theory]
    [InlineData(ChannelMode.Stereo)]
    [InlineData(ChannelMode.LeftOnly)]
    [InlineData(ChannelMode.RightOnly)]
    public void Channel_map_and_saturation_apply_without_second_gain_or_effects(ChannelMode channels)
    {
        var timeline = new TimelineBuffer(44100, 8, 0);
        timeline.WriteFrames(0, [0.25f, -0.5f, 2, -2, float.NaN, float.PositiveInfinity, float.MaxValue, float.MinValue]);
        var reader = new DlnaTimelineReader(timeline, 0, 0, 0, channels);
        var pcm = new short[8];
        Assert.True(reader.Read(0, 1, pcm));
        short[] left = [8192, 32767, 0, 32767], right = [-16384, -32768, 0, -32768];
        for (int frame = 0; frame < 4; frame++)
        {
            Assert.Equal(channels == ChannelMode.RightOnly ? right[frame] : left[frame], pcm[frame * 2]);
            Assert.Equal(channels == ChannelMode.LeftOnly ? left[frame] : right[frame], pcm[frame * 2 + 1]);
        }
    }

    [Fact]
    public void Missing_history_gaps_future_and_underflow_are_silence_but_still_advance_the_cursor()
    {
        var timeline = new TimelineBuffer(44100, 8, 0);
        timeline.WriteFrames(0, Signal(0, 4));
        timeline.WriteFrames(6, Signal(6, 4));
        var reader = new DlnaTimelineReader(timeline, 0, 0, 0, ChannelMode.Stereo);
        var pcm = Enumerable.Repeat((short)99, 24).ToArray();
        Assert.False(reader.Read(0, 1, pcm));
        Assert.Equal(new short[4], pcm[..4]);
        AssertFrames(2, pcm[4..8]);
        Assert.Equal(new short[4], pcm[8..12]);
        AssertFrames(6, pcm[12..20]);
        Assert.Equal(new short[4], pcm[20..]);
        timeline.WriteFrames(12, Signal(12, 2));
        Assert.True(reader.Read(MediaClock.FromMs(99999), 1, pcm.AsSpan(0, 4)));
        AssertFrames(12, pcm[..4]);
        reader.Reset();
        Assert.True(double.IsNaN(reader.Position));
        Assert.True(reader.Read(timeline.TicksAt(12), 1, pcm.AsSpan(0, 4)));
        AssertFrames(12, pcm[..4]);
    }

    [Fact]
    public void Empty_and_invalid_blocks_do_not_anchor_and_invalid_parameters_are_rejected()
    {
        var timeline = new TimelineBuffer(44100, 8, 0);
        var reader = new DlnaTimelineReader(timeline, 0, 0, 0, ChannelMode.Stereo);
        Assert.True(reader.Read(long.MaxValue, 1, []));
        Assert.Throws<ArgumentException>(() => reader.Read(0, 1, new short[3]));
        Assert.Throws<ArgumentException>(() => reader.Read(0, 1, new short[706]));
        foreach (double ratio in new[] { 0, -1, double.NaN, double.PositiveInfinity, 1.1 })
            Assert.Throws<ArgumentOutOfRangeException>(() => reader.Read(0, ratio, new short[2]));
        Assert.True(double.IsNaN(reader.Position));
        Assert.Throws<ArgumentNullException>(() => new DlnaTimelineReader(null!, 0, 0, 0, ChannelMode.Stereo));
        Assert.Throws<ArgumentException>(() => new DlnaTimelineReader(new TimelineBuffer(48000, 8, 0), 0, 0, 0, ChannelMode.Stereo));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DlnaTimelineReader(timeline, -1, 0, 0, ChannelMode.Stereo));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DlnaTimelineReader(timeline, 0, 0, -1, ChannelMode.Stereo));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DlnaTimelineReader(timeline, 0, 0, 0, (ChannelMode)999));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0.999000999000999)]
    public void Direct_and_fractional_reads_allocate_nothing_per_chunk(double ratio)
    {
        var timeline = new TimelineBuffer(44100, 2000, 0);
        var reader = new DlnaTimelineReader(timeline, 0, 0, 0, ChannelMode.Stereo);
        var pcm = new short[704];
        Assert.Equal(0, Signals.Allocated(() =>
        {
            for (int i = 0; i < 1000; i++) reader.Read(0, ratio, pcm);
        }));
        Assert.All(pcm, sample => Assert.Equal(0, sample));
    }

    private static short Left(long frame) => (short)((frame % 20000 + 20000) % 20000 - 10000);

    private static float[] Signal(long first, int frames)
    {
        var signal = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            signal[i * 2] = Left(first + i) / 32767f;
            signal[i * 2 + 1] = -signal[i * 2];
        }
        return signal;
    }

    private static void AssertFrames(long first, short[] pcm)
    {
        for (int i = 0; i < pcm.Length / 2; i++)
        {
            Assert.Equal(Left(first + i), pcm[i * 2]);
            Assert.Equal(-Left(first + i), pcm[i * 2 + 1]);
        }
    }
}
