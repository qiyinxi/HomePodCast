using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class RaopTimelineReaderTests
{
    [Theory]
    [InlineData(1000, 1025, -10)]
    [InlineData(1000, 1025, 10)]
    [InlineData(1500, 1525, -10)]
    [InlineData(1500, 1525, 10)]
    [InlineData(2000, 2025, -10)]
    [InlineData(2000, 2025, 10)]
    public void Presentation_time_includes_transport_latency_and_group_delay_and_signed_trim(int latency, int delay, int trim)
    {
        var timeline = new TimelineBuffer(44100, 10000, 123456);
        timeline.WriteFrames(0, Signal(0, 10000));
        long deadline = timeline.OriginTicks + MediaClock.FromMs(100);
        var reader = new RaopTimelineReader(timeline, delay, trim, latency, ChannelMode.Stereo);
        var pcm = new short[704];
        Assert.True(reader.Read(deadline, pcm));
        // At 100 ms, 25 ms group headroom and +/-10 ms trim select 65/85 ms of source history.
        long first = 4410 - (long)Math.Round((25 + trim) * 44.1);
        AssertFrames(first, pcm);
    }

    [Fact]
    public void Non_integral_start_and_half_frame_delay_keep_a_thousand_packets_contiguous_for_independent_readers()
    {
        const int packets = 1000;
        var timeline = new TimelineBuffer(44100, packets * 352, 456789);
        long deadline = timeline.OriginTicks + (long)Math.Round(2_500_114 * (double)MediaClock.Frequency / 10_000_000);
        // 2025 ms is 89302.5 frames: round the delay separately from the presentation clock.
        long first = timeline.FrameAt(deadline + MediaClock.FromMs(2000)) - 89302;
        Assert.NotEqual(first, timeline.FrameAt(deadline + MediaClock.FromMs(-25)));
        timeline.WriteFrames(first, Signal(first, packets * 352));
        var full = new RaopTimelineReader(timeline, 2025, 0, 2000, ChannelMode.Stereo);
        var split = new RaopTimelineReader(timeline, 2025, 0, 2000, ChannelMode.Stereo);
        var pcm = new short[704];
        var parts = new short[704];
        for (int packet = 0; packet < packets; packet++)
        {
            long now = deadline + (long)(packet * 352.0 * MediaClock.Frequency / 44100);
            Assert.True(full.Read(now, pcm));
            // A reader can deliver smaller blocks; subsequent calls advance by 128 and 224 frames.
            Assert.True(split.Read(now, parts.AsSpan(0, 256)));
            Assert.True(split.Read(now, parts.AsSpan(256)));
            AssertFrames(first + packet * 352L, pcm);
            Assert.Equal(pcm, parts);
        }
    }

    [Fact]
    public void Overwritten_history_gaps_and_future_frames_are_silence_and_underflow_still_advances()
    {
        var timeline = new TimelineBuffer(44100, 8, 0);
        timeline.WriteFrames(0, Signal(0, 4));
        timeline.WriteFrames(6, Signal(6, 4));
        var reader = new RaopTimelineReader(timeline, 1000, 0, 1000, ChannelMode.Stereo);
        var pcm = Enumerable.Repeat((short)99, 24).ToArray();
        Assert.False(reader.Read(0, pcm));
        Assert.Equal(new short[4], pcm[..4]); // overwritten
        AssertFrames(2, pcm[4..8]);
        Assert.Equal(new short[4], pcm[8..12]); // skipped
        AssertFrames(6, pcm[12..20]);
        Assert.Equal(new short[4], pcm[20..]); // not yet written
        timeline.WriteFrames(12, Signal(12, 2));
        Assert.True(reader.Read(0, pcm.AsSpan(0, 4)));
        AssertFrames(12, pcm[..4]);
    }

    [Theory]
    [InlineData(ChannelMode.Stereo)]
    [InlineData(ChannelMode.LeftOnly)]
    [InlineData(ChannelMode.RightOnly)]
    public void Channel_selection_duplicates_the_selected_channel_without_gain(ChannelMode channels)
    {
        var timeline = new TimelineBuffer(44100, 3, 0);
        timeline.WriteFrames(0, [0.25f, -0.5f, 1, -1, -0.125f, 0.75f]);
        var reader = new RaopTimelineReader(timeline, 0, 0, 0, channels);
        var pcm = new short[6];
        Assert.True(reader.Read(0, pcm));
        short[] left = [8192, 32767, -4096], right = [-16384, -32767, 24575];
        for (int frame = 0; frame < 3; frame++)
        {
            Assert.Equal(channels == ChannelMode.RightOnly ? right[frame] : left[frame], pcm[frame * 2]);
            Assert.Equal(channels == ChannelMode.LeftOnly ? left[frame] : right[frame], pcm[frame * 2 + 1]);
        }
    }

    [Fact]
    public void Pcm_conversion_rounds_and_saturates_finite_samples_and_silences_non_finite_samples()
    {
        float[] signal = [0, -0f, 1, -1, 0.5f, -0.5f, 1.5f, -2, float.MaxValue, float.MinValue,
            float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0.5f / 32767, 1.5f / 32767, -1.5f / 32767];
        var timeline = new TimelineBuffer(44100, signal.Length / 2, 0);
        timeline.WriteFrames(0, signal);
        var reader = new RaopTimelineReader(timeline, 0, 0, 0, ChannelMode.Stereo);
        var pcm = new short[signal.Length];
        Assert.True(reader.Read(0, pcm));
        Assert.Equal(new short[] { 0, 0, 32767, -32767, 16384, -16384, 32767, -32768, 32767, -32768,
            0, 0, 0, 0, 2, -2 }, pcm);
    }

    [Fact]
    public void Empty_or_invalid_blocks_do_not_anchor_the_reader()
    {
        var timeline = new TimelineBuffer(44100, 8, 123456);
        timeline.WriteFrames(4, Signal(4, 4));
        var reader = new RaopTimelineReader(timeline, 0, 0, 0, ChannelMode.Stereo);
        Assert.True(reader.Read(long.MaxValue, []));
        Assert.Throws<ArgumentException>(() => reader.Read(0, new short[3]));
        Assert.Throws<ArgumentException>(() => reader.Read(0, new short[706]));
        var pcm = new short[8];
        Assert.True(reader.Read(timeline.TicksAt(4), pcm));
        AssertFrames(4, pcm);
    }

    [Fact]
    public void Constructor_rejects_invalid_timeline_delay_latency_and_channels()
    {
        var timeline = new TimelineBuffer(44100, 352, 0);
        Assert.Throws<ArgumentNullException>(() => new RaopTimelineReader(null!, 0, 0, 0, ChannelMode.Stereo));
        Assert.Throws<ArgumentException>(() => new RaopTimelineReader(new TimelineBuffer(48000, 352, 0), 0, 0, 0, ChannelMode.Stereo));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RaopTimelineReader(timeline, -1, 0, 0, ChannelMode.Stereo));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RaopTimelineReader(timeline, 0, 0, -1, ChannelMode.Stereo));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RaopTimelineReader(timeline, 0, 0, 0, (ChannelMode)999));
    }

    [Theory]
    [InlineData(ChannelMode.Stereo)]
    [InlineData(ChannelMode.LeftOnly)]
    [InlineData(ChannelMode.RightOnly)]
    public void History_reads_and_conversion_do_not_allocate_per_packet(ChannelMode channels)
    {
        var timeline = new TimelineBuffer(44100, 352, 0);
        var signal = Signal(0, 352);
        var reader = new RaopTimelineReader(timeline, 0, 0, 0, channels);
        var pcm = new short[704];
        long frame = 0;
        Assert.Equal(0, Signals.Allocated(() =>
        {
            for (int packet = 0; packet < 1000; packet++)
            {
                timeline.WriteFrames(frame, signal);
                reader.Read(timeline.TicksAt(frame), pcm);
                frame += 352;
            }
        }));
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
