using System.Diagnostics;
using HomePodCast.Net;

namespace HomePodCast.Tests;

/// <summary>
/// The 5.1 sender paces on the HomePod's clock (experimental since 1.4): whatever QPC and that clock drift apart, block n
/// leaves when the HomePod's time is the anchor's start + n·1024/48000, so the lead stays the anchor latency.
/// </summary>
public sealed class SurroundPacingTests
{
    private const long Second = 1_000_000_000;

    /// <summary>The HomePod's time at a QPC instant, for an offset (HomePod time minus LocalNs).</summary>
    private static long HomePodAt(long qpc, long offsetNs) => PtpClockObserver.LocalNs(qpc) + offsetNs;

    [Fact]
    public void Block_zero_leaves_at_the_anchors_start()
    {
        long startAt = Stopwatch.GetTimestamp();
        const long offset = 1_700_000_000 * Second;
        ulong startHp = (ulong)HomePodAt(startAt, offset);
        Assert.InRange(SurroundPacing.DeadlineQpc(startHp, 0, 48000, offset), startAt - 1, startAt + 1);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(-100)]
    [InlineData(0)]
    public void Deadlines_follow_the_HomePods_clock_through_an_hour_of_drift(int ppm)
    {
        long startAt = Stopwatch.GetTimestamp();
        const long offset0 = 1_700_000_000 * Second;
        ulong startHp = (ulong)HomePodAt(startAt, offset0);
        long worstNs = 0, lastDeadline = startAt;
        for (long frames = 0; frames <= 48000L * 3600; frames += 1024 * 97)
        {
            // The offset the follower reports by then: drifting ppm against QPC since the start.
            long elapsedNs = frames * Second / 48000;
            long offset = offset0 + elapsedNs * ppm / 1_000_000;
            long deadline = SurroundPacing.DeadlineQpc(startHp, frames, 48000, offset);
            long error = HomePodAt(deadline, offset) - ((long)startHp + elapsedNs);
            worstNs = Math.Max(worstNs, Math.Abs(error));
            Assert.True(deadline >= lastDeadline, "deadlines never go back");
            lastDeadline = deadline;
        }
        Assert.True(worstNs <= 1_000, $"HomePod time at a deadline was off by {worstNs} ns");
        // Paced on QPC alone, the last block would have left this far from where the HomePod expects it.
        long qpcOnly = startAt + (long)(3600.0 * Stopwatch.Frequency);
        long driftNs = Math.Abs(HomePodAt(qpcOnly, offset0 + 3600 * Second * ppm / 1_000_000) - ((long)startHp + 3600 * Second));
        long expected = Math.Abs(ppm) * 3600 * 1000L; // 100 ppm: 360 ms an hour
        Assert.InRange(driftNs, expected - 1_000, expected + 1_000);
    }
}
