using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class LocalOutputClockTests
{
    [Fact]
    public void Native_clock_units_and_correlated_QPC_are_converted_before_comparison()
    {
        var position = RenderPosition.FromNative(48000 * 4, 48000 * 4, 5_000_000, 48000);
        Assert.Equal(48000, position.Frames);
        Assert.Equal(MediaClock.FromMs(500), position.QpcTicks);
    }

    [Theory]
    [InlineData(-1000)]
    [InlineData(0)]
    [InlineData(1000)]
    public void Device_clock_drift_does_not_accumulate_into_presentation_delay(int ppm)
    {
        const int rate = 48000;
        double actualRate = rate * (1 + ppm * 1e-6);
        var clock = new RenderClock(rate);
        for (long period = 0; period < 360000; period += 13)
        {
            double played = period * 480;
            long ticks = (long)Math.Round(played * MediaClock.Frequency / actualRate);
            var position = new RenderPosition(played, ticks);
            long presentation = clock.PresentationTicks(played + 960, position, 200);
            double expectedMs = (played + 960) * 1000 / actualRate + 200;
            Assert.InRange(Math.Abs(MediaClock.ToMs(presentation) - expectedMs), 0, 0.03);
        }
        Assert.InRange(Math.Abs(clock.Rate - actualRate), 0, 0.01);
    }

    [Fact]
    public void Sinc_reader_uses_presentation_delay_and_trim_without_an_extra_filter_delay()
    {
        var timeline = new TimelineBuffer(44100, 44100, 0);
        var input = new float[4410 * 2];
        input[2205 * 2] = 1;
        input[2205 * 2 + 1] = -1;
        timeline.Write(0, input);
        var reader = new ClockedTimelineReader(timeline, 48000, 4800);
        var output = new float[4800 * 2];
        reader.Read(MediaClock.FromMs(230), 200, 30, 48000, output);
        int peak = Enumerable.Range(0, output.Length / 2).MaxBy(i => Math.Abs(output[i * 2]));
        Assert.InRange(peak, 2399, 2401);
        Assert.InRange(output[peak * 2], 0.8f, 1.01f);
        Assert.Equal(-output[peak * 2], output[peak * 2 + 1]);
    }

    [Fact]
    public void Sinc_reader_is_allocation_free_after_setup()
    {
        var timeline = new TimelineBuffer(44100, 44100, 0);
        timeline.WriteFrames(0, Enumerable.Repeat(0.25f, 44100 * 2).ToArray());
        var reader = new ClockedTimelineReader(timeline, 48000, 480);
        var pcm = new float[960];
        Assert.Equal(0, Signals.Allocated(() =>
        {
            for (int i = 0; i < 100; i++) reader.Read(MediaClock.FromMs(500), 200, 0, 48000.5, pcm);
        }));
        Assert.All(pcm, v => Assert.InRange(v, 0.2499f, 0.2501f));
    }
}
