using System.Diagnostics;
using HomePodCast.Audio;

namespace HomePodCast.Tests;

/// <summary>
/// The once-per-process check behind the 5.1 encoder pre-check (experimental since 1.4): a slow answer is kept for the
/// next attempt instead of a timeout counting as "no" for good.
/// </summary>
public sealed class BackgroundCheckTests
{
    [Fact]
    public void A_slow_answer_refuses_only_the_attempt_that_timed_out()
    {
        using var release = new ManualResetEventSlim();
        int probes = 0;
        ApartmentState apartment = ApartmentState.Unknown;
        var check = new BackgroundCheck("test", () =>
        {
            Interlocked.Increment(ref probes);
            apartment = Thread.CurrentThread.GetApartmentState();
            release.Wait();
            return (true, "ok");
        }, TimeSpan.FromMilliseconds(200));

        Assert.False(check.Passed()); // no answer within the budget: this attempt streams stereo
        release.Set();
        var sw = Stopwatch.StartNew();
        while (!check.Passed())
        {
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), "the late answer was never taken");
            Thread.Sleep(10);
        }
        Assert.True(check.Passed());
        Assert.Equal(1, probes); // the late answer, not a second probe
        Assert.Equal(ApartmentState.MTA, apartment);
    }

    [Fact]
    public void A_probe_that_hangs_costs_only_the_first_attempt_its_wait()
    {
        using var never = new ManualResetEventSlim();
        int probes = 0;
        var check = new BackgroundCheck("test", () =>
        {
            Interlocked.Increment(ref probes);
            never.Wait();
            return (true, "too late");
        }, TimeSpan.FromSeconds(1));

        Assert.False(check.Passed());
        var sw = Stopwatch.StartNew();
        Assert.False(check.Passed());
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(900), $"waited {sw.ElapsedMilliseconds} ms again");
        Assert.Equal(1, probes);
        never.Set();
    }

    [Fact]
    public void A_no_or_a_failure_is_kept()
    {
        int probes = 0;
        var no = new BackgroundCheck("test", () => { Interlocked.Increment(ref probes); return (false, "no 6-channel types"); }, TimeSpan.FromSeconds(5));
        Assert.False(no.Passed());
        Assert.False(no.Passed());
        var throws = new BackgroundCheck("test", () => { Interlocked.Increment(ref probes); throw new NotSupportedException("no"); }, TimeSpan.FromSeconds(5));
        Assert.False(throws.Passed());
        Assert.False(throws.Passed());
        Assert.Equal(2, probes);
    }
}
