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

    [Theory]
    [InlineData(-1000)]
    [InlineData(-300)]
    [InlineData(300)]
    [InlineData(1000)]
    public void An_hour_of_bursty_receiver_reads_keeps_presentation_within_rough_DLNA_accuracy(int ppm)
    {
        var clock = new DlnaReadClock();
        var timeline = new TimelineBuffer(44100, 2048, 0);
        var marker = new float[2048];
        var pcm = new short[128];
        double physicalRate = 44100 * (1 + ppm * 1e-6);
        double sourcePosition = 0, epochSeconds = 0, maximumErrorMs = 0;
        long previousPlayed = 0;
        bool paused = false;
        int estimates = 0, probes = 0;
        // The fake advances one quarter-second at a time. It carries counts, not an hour-sized PCM recording.
        for (int step = 0; step <= 14400; step++)
        {
            double seconds = step / 4.0;
            if (seconds >= 1800 && seconds < 1806)
            {
                clock.Reset();
                paused = true;
                Assert.False(clock.HasEstimate);
                continue;
            }
            if (paused)
            {
                epochSeconds = seconds;
                sourcePosition = seconds * 44100;
                previousPlayed = 0;
                paused = false; // A resumed HTTP stream takes a fresh timeline anchor.
            }
            double activeSeconds = seconds - epochSeconds;
            long played = (long)Math.Round(activeSeconds * physicalRate);
            // The fake renderer consumes its physical frame clock independently of completion scheduling.
            sourcePosition += (played - previousPlayed) * clock.SourceFramesPerOutputFrame;
            previousPlayed = played;
            // A socket drains in 180 ms bursts. Completion time has jitter and occasional delayed writes;
            // the eight-second initial prefill is an intercept, never evidence for a receiver clock.
            double readSeconds = Math.Floor(activeSeconds / 0.18) * 0.18;
            long completed = 8 * 44100L + (long)Math.Round(readSeconds * physicalRate);
            double jitter = 0.025 * Math.Sin(step * 1.73) + 0.015 * Math.Sin(step * 0.13)
                + (step % 113 == 0 ? 0.10 : 0);
            bool eligible = activeSeconds >= 3;
            clock.Observe(completed, Ticks(seconds + jitter), eligible);
            if (activeSeconds < 48) Assert.False(clock.HasEstimate);
            if (clock.HasEstimate) estimates++;
            double errorMs = Math.Abs(sourcePosition / 44.1 - seconds * 1000);
            maximumErrorMs = Math.Max(maximumErrorMs, errorMs);
            Assert.InRange(errorMs, 0, 500);
            if (activeSeconds < 65 || step % 240 != 40) continue;
            // Sparse real PCM probes encode source time in sample amplitude. They check which source marker
            // would actually be heard, independently of the fake's consumption-counter arithmetic.
            long first = (long)Math.Round(sourcePosition);
            WriteTimestampMarker(timeline, first - 128, marker);
            var reader = new DlnaTimelineReader(timeline, 0, 0, 0, ChannelMode.Stereo);
            Assert.True(reader.Read(timeline.TicksAt(first), clock.SourceFramesPerOutputFrame, pcm));
            double markerSeconds = Math.Floor(sourcePosition / (44100 * 60)) * 60
                + (pcm[0] / 32767.0 + 1) * 30;
            Assert.InRange(Math.Abs(markerSeconds - seconds) * 1000, 0, 501);
            Assert.Equal(-pcm[0], pcm[1]);
            probes++;
        }
        Assert.True(estimates > 12000, $"Only {estimates} eligible estimates in the simulated hour");
        Assert.True(probes > 50);
        Assert.True(maximumErrorMs < 500, $"Worst presentation error {maximumErrorMs:F1} ms");
        Assert.True(clock.HasEstimate);
        Assert.InRange(Math.Abs(clock.DriftPpm - ppm), 0, 180);
    }

    private static void WriteTimestampMarker(TimelineBuffer timeline, long first, float[] marker)
    {
        const long period = 44100 * 60;
        for (int i = 0; i < marker.Length / 2; i++)
        {
            float value = (float)(((first + i) % period) * 2.0 / period - 1);
            marker[i * 2] = value;
            marker[i * 2 + 1] = -value;
        }
        timeline.WriteFrames(first, marker);
    }

    private static void Feed(DlnaReadClock clock, int first, int last)
    {
        for (int second = first; second <= last; second++) clock.Observe(second * 44100L, Ticks(second), true);
    }

    private static long Ticks(double seconds) => (long)Math.Round(seconds * MediaClock.Frequency);
}
