using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class DlnaReadClockTests
{
    [Theory]
    [InlineData(-1000)]
    [InlineData(-300)]
    [InlineData(300)]
    [InlineData(1000)]
    public void Sustained_backpressure_observes_the_receiver_rate_and_inverts_its_sign_for_source_reading(int ppm)
    {
        var clock = new DlnaReadClock();
        for (int quarter = 0; quarter <= 240; quarter++)
        {
            double seconds = quarter / 4.0;
            clock.Observe((long)Math.Round(seconds * 44100 * (1 + ppm * 1e-6)), Ticks(seconds), true);
            if (seconds < 45) Assert.False(clock.HasEstimate);
        }
        Assert.True(clock.HasEstimate);
        Assert.InRange(clock.Confidence, 0.99, 1);
        Assert.InRange(Math.Abs(clock.DriftPpm - ppm), 0, 0.1);
        Assert.Equal(1 / (1 + clock.DriftPpm / 1e6), clock.SourceFramesPerOutputFrame);
        Assert.Equal(ppm < 0, clock.SourceFramesPerOutputFrame > 1);
    }

    [Fact]
    public void Initial_prefill_and_an_hour_of_sender_pacing_do_not_report_a_receiver_clock()
    {
        var clock = new DlnaReadClock();
        clock.Observe(44100 * 8, 0, false);
        for (int second = 1; second <= 3600; second++)
        {
            clock.Observe((second + 8L) * 44100, Ticks(second), false);
            Assert.False(clock.HasEstimate);
            Assert.Equal(0, clock.Confidence);
            Assert.Equal(1, clock.SourceFramesPerOutputFrame);
        }
    }

    [Fact]
    public void Reconnect_pause_and_loss_of_backpressure_revoke_confidence_and_require_a_new_window()
    {
        var clock = new DlnaReadClock();
        Feed(clock, 0, 60);
        Assert.True(clock.HasEstimate);
        clock.Observe(44100 * 64, Ticks(64), true);
        Assert.False(clock.HasEstimate);
        Feed(clock, 65, 90);
        Assert.False(clock.HasEstimate);
        Feed(clock, 91, 125);
        Assert.True(clock.HasEstimate);
        clock.Observe(44100 * 126, Ticks(126), false);
        Assert.False(clock.HasEstimate);
        Feed(clock, 127, 185);
        Assert.True(clock.HasEstimate);
        clock.Observe(0, Ticks(186), true); // New HTTP request's PCM count.
        Assert.False(clock.HasEstimate);
        clock.Reset();
        Assert.Equal(0, clock.DriftPpm);
        Assert.Equal(0, clock.Confidence);
    }

    [Fact]
    public void An_eligible_but_implausibly_fast_prefill_or_unstable_clock_is_not_published()
    {
        var clock = new DlnaReadClock();
        for (int quarter = 0; quarter <= 240; quarter++)
            clock.Observe(quarter * 22050L, Ticks(quarter / 4.0), true);
        Assert.False(clock.HasEstimate);
        Assert.Equal(0, clock.DriftPpm);
        Assert.Equal(1, clock.SourceFramesPerOutputFrame);
    }

    [Fact]
    public void Observation_and_regression_do_not_allocate_per_completed_write()
    {
        var clock = new DlnaReadClock();
        long frame = 0;
        double seconds = 0;
        Assert.Equal(0, Signals.Allocated(() =>
        {
            for (int i = 0; i < 1000; i++)
            {
                clock.Observe(frame, Ticks(seconds), true);
                frame += 11025;
                seconds += 0.25;
            }
        }));
        Assert.True(clock.HasEstimate);
    }

    private static void Feed(DlnaReadClock clock, int first, int last)
    {
        for (int second = first; second <= last; second++) clock.Observe(second * 44100L, Ticks(second), true);
    }

    private static long Ticks(double seconds) => (long)Math.Round(seconds * MediaClock.Frequency);
}
