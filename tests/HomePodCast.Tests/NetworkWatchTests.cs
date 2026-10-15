using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class NetworkWatchTests
{
    private const string Pings = @"\d+/\d+/\d+ms lost=(\d+)/(\d+)";

    [Fact]
    public async Task Summarizes_round_trips_and_resets_after_each_summary()
    {
        using var watch = new NetworkWatch(IPAddress.Loopback, IPAddress.Parse("127.0.0.2")).Start();
        // CI runners can be slow to start the ping loops: wait (up to 5 s) until both targets have answered.
        Match first = Match.Empty;
        for (int i = 0; i < 50 && !first.Success; i++)
        {
            await Task.Delay(100);
            first = Regex.Match(watch.TakeSummary(), $"^ping={Pings} router={Pings}$");
        }
        Assert.True(first.Success, "no answers from the loopback addresses");
        Assert.Equal("0", first.Groups[1].Value); // nothing lost
        Assert.Equal("0", first.Groups[3].Value);
        Assert.True(watch.WatchesRouter); // it answered

        // Each summary starts over: right after one, at most a couple of pings are counted.
        var again = Regex.Match(watch.TakeSummary(), $@"^ping=(-|{Pings}) router=(-|{Pings})$");
        Assert.True(again.Success);
        foreach (int g in new[] { 3, 6 })
            if (again.Groups[g].Success) Assert.True(int.Parse(again.Groups[g].Value) <= 2);
    }

    [Fact]
    public async Task A_router_that_never_answered_is_not_watched_and_raises_no_spikes()
    {
        // A router that drops pings to itself must not look like the PC's Wi-Fi failing on every ping.
        var spikes = new ConcurrentQueue<long>();
        using var watch = new NetworkWatch(IPAddress.Loopback, IPAddress.Parse("192.0.2.1")); // TEST-NET-1: nobody answers
        watch.Spike += (target, rtt) => { if (target == PingTarget.Router) spikes.Enqueue(rtt); };
        watch.Start();
        await Task.Delay(1500);
        Assert.False(watch.WatchesRouter);
        Assert.Empty(spikes);
    }

    [Fact]
    public void Same_subnet_uses_the_mask()
    {
        var mask = IPAddress.Parse("255.255.255.0");
        Assert.True(NetworkWatch.SameSubnet(IPAddress.Parse("192.168.50.10"), IPAddress.Parse("192.168.50.226"), mask));
        Assert.False(NetworkWatch.SameSubnet(IPAddress.Parse("192.168.51.10"), IPAddress.Parse("192.168.50.226"), mask));
    }

    [Fact]
    public void Windows_gets_long_enough_that_only_a_missing_answer_times_out_before_500_ms()
    {
        // Windows ends a timed-out echo request on its next 500 ms tick: a 500 ms timeout gave up 1–32 ms after sending
        // (2026-10-14). Two ticks guarantee one whole tick of waiting, and that is the loss threshold.
        Assert.True(NetworkWatch.IcmpTimeoutMs - NetworkWatch.WindowsIcmpTickMs >= NetworkWatch.TimeoutMs);
    }

    [Theory]
    [InlineData(IPStatus.Success, 2, 2)]
    [InlineData(IPStatus.Success, 500, 500)]
    [InlineData(IPStatus.Success, 501, -1)] // answered, but later than "lost" allows
    [InlineData(IPStatus.TimedOut, 0, -1)]
    [InlineData(IPStatus.DestinationHostUnreachable, 0, -1)]
    public void Counts_an_answer_within_the_loss_threshold(IPStatus status, long replyMs, long counted) =>
        Assert.Equal(counted, NetworkWatch.RoundTrip(new NetworkWatch.EchoResult(status, replyMs)));

    [Theory]
    [InlineData(0, 100, 100)]
    [InlineData(NetworkWatch.ErrorsBeforeBackoff - 1, 100, 100)]
    [InlineData(NetworkWatch.ErrorsBeforeBackoff, 100, NetworkWatch.BackoffMs)]
    [InlineData(NetworkWatch.ErrorsBeforeBackoff + 50, 0, NetworkWatch.BackoffMs)]
    [InlineData(NetworkWatch.ErrorsBeforeBackoff, 2000, 2000)]
    public void Slows_down_to_once_a_second_while_pings_cannot_be_sent(int errors, int intervalMs, int expected) =>
        Assert.Equal(expected, NetworkWatch.IntervalAfter(errors, intervalMs));

    [Fact]
    public async Task A_ping_that_cannot_be_sent_is_no_loss_and_starts_over_with_a_new_sender()
    {
        int created = 0, calls = 0;
        var spikes = new ConcurrentQueue<long>();
        // The router is the speaker here, so only the speaker is pinged.
        using (var watch = new NetworkWatch(IPAddress.Loopback, IPAddress.Loopback, 0, () =>
               {
                   Interlocked.Increment(ref created);
                   return new FakeEcho(() => Interlocked.Increment(ref calls) % 3 == 0
                       ? throw new PingException("no ICMP handle")
                       : new NetworkWatch.EchoResult(IPStatus.Success, 2));
               }))
        {
            watch.Spike += (_, rtt) => spikes.Enqueue(rtt);
            watch.Start();
            await WaitFor(() => Volatile.Read(ref calls) >= 300);
            var summary = watch.TakeSummary();
            var m = Regex.Match(summary, @"^ping=2/2/2ms lost=0/(\d+) unsent=(\d+)$");
            Assert.True(m.Success, summary);
            Assert.True(int.Parse(m.Groups[2].Value) > 0);
        }
        await Task.Delay(200); // the probe ends after the ping under way
        Assert.Empty(spikes);
        Assert.True(Volatile.Read(ref created) >= Volatile.Read(ref calls) / 3, $"{created} senders for {calls} pings");
    }

    [Fact]
    public async Task A_late_answer_is_a_lost_ping_and_another_status_is_named_in_the_log()
    {
        int calls = 0;
        var spikes = new ConcurrentQueue<long>();
        var lines = new ConcurrentQueue<string>();
        void OnLog(string line)
        {
            if (line.Contains("network: ping to the speaker lost")) lines.Enqueue(line);
        }
        Log.Written += OnLog;
        try
        {
            using var watch = new NetworkWatch(IPAddress.Loopback, IPAddress.Loopback, 0, () => new FakeEcho(() => (Interlocked.Increment(ref calls) % 3) switch
            {
                0 => new NetworkWatch.EchoResult(IPStatus.Success, 700), // after the 500 ms that count
                1 => new NetworkWatch.EchoResult(IPStatus.Success, 2),
                _ => new NetworkWatch.EchoResult(IPStatus.DestinationHostUnreachable, 0),
            }));
            watch.Spike += (_, rtt) => spikes.Enqueue(rtt);
            watch.Start(); // the second ping is lost: its spike is in before the third ping is made
            await WaitFor(() => Volatile.Read(ref calls) >= 300 && !lines.IsEmpty);
            var m = Regex.Match(watch.TakeSummary(), @"^ping=2/2/2ms lost=(\d+)/(\d+)$");
            Assert.True(m.Success);
            Assert.InRange(int.Parse(m.Groups[1].Value) * 3, int.Parse(m.Groups[2].Value) * 2 - 6, int.Parse(m.Groups[2].Value) * 2 + 6);
        }
        finally
        {
            Log.Written -= OnLog;
        }
        Assert.Contains(-1L, spikes);
        Assert.DoesNotContain(spikes, rtt => rtt >= 0); // 2 ms answers are no spikes, and 700 ms is no round trip
        Assert.Contains(lines, l => l.EndsWith("lost (DestinationHostUnreachable)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pings_nothing_before_Start_so_a_handler_gets_the_first_spike()
    {
        // 2026-10-15: the constructor started the probes, and with a fake that answers at once a probe could make all its
        // pings before the test subscribed; CI found no spike at all (run 38710133779).
        int calls = 0;
        var spikes = new ConcurrentQueue<long>();
        using var watch = new NetworkWatch(IPAddress.Loopback, IPAddress.Loopback, 0, () => new FakeEcho(() => Interlocked.Increment(ref calls) switch
        {
            2 => new NetworkWatch.EchoResult(IPStatus.TimedOut, 0), // the only spike: the first answer came before it
            _ => new NetworkWatch.EchoResult(IPStatus.Success, 2),
        }));
        await Task.Delay(100); // a started probe would have pinged thousands of times by now
        Assert.Equal(0, Volatile.Read(ref calls));
        watch.Spike += (_, rtt) => spikes.Enqueue(rtt);
        watch.Start();
        await WaitFor(() => !spikes.IsEmpty);
        Assert.Equal(new[] { -1L }, spikes);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), "the probe did not ping");
            await Task.Delay(20);
        }
    }

    private sealed class FakeEcho(Func<NetworkWatch.EchoResult> answer) : NetworkWatch.IEcho
    {
        public NetworkWatch.EchoResult Send(IPAddress target, int timeoutMs)
        {
            Assert.Equal(NetworkWatch.IcmpTimeoutMs, timeoutMs);
            return answer();
        }

        public void Dispose() { }
    }
}

/// <summary>Alone: it counts the whole process's handles.</summary>
[CollectionDefinition("Handle count", DisableParallelization = true)]
public sealed class HandleCountCollection;

[Collection("Handle count")]
public class NetworkWatchSoakTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>
    /// Pings 127.0.0.1 and 127.0.0.2 back to back for a few seconds, as many pings as a day and a half of streaming
    /// would send to begin with, and never forces a garbage collection: the handle count has to stay where it was, not
    /// just come back after the finalizers ran. 2026-10-14: with SendPingAsync each ping left a kernel event for the
    /// finalizer, 20 handles a second on the maintainer's PC (8565 at most).
    /// </summary>
    [Theory]
    [InlineData(0, 5000)] // back to back
    [InlineData(5, 100)]  // with a wait between two pings, as in the app
    public async Task Pinging_for_a_while_keeps_the_handle_count_flat(int intervalMs, int atLeast)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        int before = process.HandleCount, most = before;
        long pings = 0;
        var summary = new Regex(@"(?:ping|router)=\S+ lost=\d+/(\d+)");
        using (var watch = new NetworkWatch(IPAddress.Loopback, IPAddress.Parse("127.0.0.2"), intervalMs, NetworkWatch.SystemEchoFactory).Start())
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(4))
            {
                await Task.Delay(50);
                process.Refresh();
                most = Math.Max(most, process.HandleCount);
                foreach (Match m in summary.Matches(watch.TakeSummary())) pings += long.Parse(m.Groups[1].Value);
            }
        }
        output.WriteLine($"{pings} pings, handle count {before} → at most {most}");
        Assert.True(pings >= atLeast, $"only {pings} pings in 4 s");
        Assert.True(most - before < 200, $"the handle count rose by {most - before} over {pings} pings");
    }
}
