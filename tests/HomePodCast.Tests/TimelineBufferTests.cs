using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class TimelineBufferTests
{
    [Fact]
    public void Independent_readers_can_read_the_same_audio_at_different_paces()
    {
        var timeline = new TimelineBuffer(44100, 16, 123456);
        float[] signal = Enumerable.Range(1, 24).Select(i => (float)i).ToArray();
        timeline.Write(123456, signal);
        var fast = timeline.CreateReader(123456);
        var slow = timeline.CreateReader(123456);
        var all = new float[24];
        var part = new float[8];
        Assert.True(fast.Read(all));
        Assert.Equal(signal, all);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(slow.Read(part));
            Assert.Equal(signal[(i * 8)..(i * 8 + 8)], part);
        }
        Assert.Equal(fast.Position, slow.Position);
    }

    [Fact]
    public void Wrap_overwrite_gaps_and_future_reads_are_silence_without_moving_other_readers()
    {
        var timeline = new TimelineBuffer(1000, 4, 0);
        timeline.WriteFrames(0, [1, 2, 3, 4, 5, 6]);
        timeline.WriteFrames(5, [11, 12, 13, 14]);
        var actual = Enumerable.Repeat(99f, 16).ToArray();
        Assert.False(timeline.ReadFrames(0, actual));
        Assert.Equal(new float[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 11, 12, 13, 14, 0, 0 }, actual);
        Assert.Throws<ArgumentOutOfRangeException>(() => timeline.WriteFrames(6, [1, 2]));
        Assert.True(timeline.ReadFrames(5, actual.AsSpan(0, 4)));
        Assert.Equal(new float[] { 11, 12, 13, 14 }, actual[..4]);
    }

    [Fact]
    public void Oversized_write_keeps_only_the_latest_capacity()
    {
        var timeline = new TimelineBuffer(1000, 2, 0);
        timeline.WriteFrames(0, [1, 2, 3, 4, 5, 6]);
        var actual = new float[6];
        Assert.False(timeline.ReadFrames(0, actual));
        Assert.Equal(new float[] { 0, 0, 3, 4, 5, 6 }, actual);
    }

    [Fact]
    public void Presentation_delay_and_positive_trim_select_the_same_source_frames()
    {
        var timeline = new TimelineBuffer(1000, 100, 456789);
        timeline.WriteFrames(0, Enumerable.Range(1, 100).Select(i => (float)i).ToArray());
        var a = new float[8];
        var b = new float[8];
        Assert.True(timeline.ReadAt(timeline.TicksAt(2020), 2000, 0, a));
        Assert.True(timeline.ReadAt(timeline.TicksAt(2025), 2000, 5, b));
        Assert.Equal(new float[] { 41, 42, 43, 44, 45, 46, 47, 48 }, a);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Packet_clock_does_not_accumulate_rounding_over_an_hour()
    {
        var timeline = new TimelineBuffer(44100, 352, MediaClock.Now);
        for (long packet = 0; packet < 451023; packet += 17)
        {
            long frame = packet * 352;
            Assert.Equal(frame, timeline.FrameAt(timeline.TicksAt(frame)));
        }
        Assert.Equal(-352, timeline.FrameAt(timeline.TicksAt(-352)));
    }

    [Fact]
    public void Read_and_write_do_not_allocate()
    {
        var timeline = new TimelineBuffer(44100, 44100, 0);
        var pcm = new float[704];
        long frame = 0;
        Assert.Equal(0, Signals.Allocated(() =>
        {
            timeline.WriteFrames(frame, pcm);
            Assert.True(timeline.ReadFrames(frame, pcm));
            frame += 352;
        }));
    }
}
