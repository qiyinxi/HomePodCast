using HomePodCast.Audio;

namespace HomePodCast.Tests;

public class FifoCeilingTests
{
    [Theory]
    [InlineData(40, 16, 30)]    // low latency: the old fixed 30 ms
    [InlineData(100, 16, 50)]   // half of the latency
    [InlineData(126, 16, 63)]
    [InlineData(200, 16, 100)]  // 2026-11-15: 200 ms covers the 100 ms stall in the maintainer's log
    [InlineData(2000, 16, 100)] // at most 100 ms
    [InlineData(60, 40, 40)]    // never below the configured target
    public void The_fifo_may_use_half_of_the_latency_between_30_and_100_ms(int latencyMs, int fifoTargetMs, int expected) =>
        Assert.Equal(expected, StreamController.FifoCeilingFor(latencyMs, fifoTargetMs));

    [Fact]
    public void A_stall_log_counts_empty_waits_apart_from_the_threads_work()
    {
        // 2026-11-15: "this thread worked 98 ms" while Windows delivered nothing for 100 ms and the thread only waited.
        string text = RoutedCapture.StallMessage(gapMs: 100, lastWaitMs: 1, signalled: true, emptyWaits: 8, emptyWaitMs: 96,
            workedMs: 2, checkMs: 0, arrivedMs: 10);
        Assert.Contains("8 waits of 96 ms in all ended without one", text);
        Assert.Contains("this thread worked 2 ms", text);
        Assert.StartsWith("capture: 100 ms without a packet while audio played", text);

        string single = RoutedCapture.StallMessage(17, 17, true, 0, 0, 0, 0, 10);
        Assert.DoesNotContain("ended without one", single);
        Assert.Contains("waited 17 ms for Windows' signal, which came; this thread worked 0 ms", single);
    }
}
