using System.Diagnostics;

namespace HomePodCast.Tests;

/// <summary>
/// Native.PreciseTimer.WaitFor: the capture's wait for Windows' "packet ready" signal. 2026-11-10: WaitOne(20) ended on the
/// system timer's tick (32 ms at the median on the maintainer's PC), so a packet whose signal did not come was fetched
/// 31 to 47 ms after the one before: more than the 20 ms capture buffer held, and more than the FIFO's 30 ms.
/// </summary>
public class PreciseTimerTests
{
    [Fact]
    public void A_signalled_wait_returns_at_once_and_takes_the_signal()
    {
        using var timer = new Native.PreciseTimer();
        using var signal = new AutoResetEvent(true);
        IntPtr handle = signal.SafeWaitHandle.DangerousGetHandle();
        var sw = Stopwatch.StartNew();
        Assert.True(timer.WaitFor(handle, Stopwatch.Frequency)); // a second at most: it never waits that long
        Assert.True(sw.ElapsedMilliseconds < 500);
        Assert.False(timer.WaitFor(handle, 0));                   // an auto-reset event: taken by the wait above
    }

    [Fact]
    public void A_wait_nobody_signals_ends_near_its_time_not_on_the_system_timers_tick()
    {
        using var timer = new Native.PreciseTimer();
        using var signal = new AutoResetEvent(false);
        IntPtr handle = signal.SafeWaitHandle.DangerousGetHandle();
        long twelve = Stopwatch.Frequency * 12 / 1000;
        var took = new List<double>();
        for (int i = 0; i < 21; i++)
        {
            long from = Stopwatch.GetTimestamp();
            Assert.False(timer.WaitFor(handle, twelve));
            took.Add((Stopwatch.GetTimestamp() - from) * 1000.0 / Stopwatch.Frequency);
        }
        took.Sort();
        // The median, so that a busy test machine's stragglers don't count: 12 ms, where two 15.6 ms ticks are 31.
        Assert.InRange(took[10], 11.0, 20.0);
    }

    [Fact]
    public async Task A_signal_that_comes_during_the_wait_ends_it()
    {
        using var timer = new Native.PreciseTimer();
        using var signal = new AutoResetEvent(false);
        IntPtr handle = signal.SafeWaitHandle.DangerousGetHandle();
        var set = Task.Run(async () =>
        {
            await Task.Delay(50);
            signal.Set();
        });
        var sw = Stopwatch.StartNew();
        Assert.True(timer.WaitFor(handle, Stopwatch.Frequency * 10));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        await set;
    }
}
