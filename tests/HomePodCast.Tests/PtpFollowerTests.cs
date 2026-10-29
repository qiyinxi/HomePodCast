using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using HomePodCast.Net;

namespace HomePodCast.Tests;

/// <summary>
/// Following a HomePod's own PTP clock for 5.1 surround (experimental since 1.4): the observer from synthetic packets,
/// the follower against the fake grandmaster on loopback. No speaker, no standard PTP port.
/// </summary>
public sealed class PtpFollowerTests
{
    private static readonly IPAddress HomePod = IPAddress.Parse("192.0.2.26");
    private const ulong HomePodClock = 0x1122334455667788, Ours = 0x0123456789abcdef;
    private const long Second = 1_000_000_000;

    [Fact]
    public void Sync_and_Follow_Up_give_the_offset_and_the_timeline()
    {
        var observer = new PtpClockObserver(HomePod, Ours);
        const long offset = 4 * Second, delay = 300_000; // its clock minus ours; path delay
        observer.ObserveCore(new(HomePod, 320), PtpPackets.AnnounceMessage(HomePodClock, 1, 0, priority: 248), 320, 900_000_000);
        long t1 = 5 * Second, t2 = t1 - offset + delay;
        observer.ObserveCore(new(HomePod, 319), PtpPackets.SyncMessage(HomePodClock, 7), 319, t2);
        Assert.Null(observer.OffsetNs); // two-step: nothing until its Follow_Up
        observer.ObserveCore(new(HomePod, 320), PtpPackets.FollowUpMessage(HomePodClock, 7, (ulong)t1), 320, t2 + 50_000);
        Assert.Equal(offset - delay, observer.OffsetNs);
        Assert.Equal(HomePodClock, observer.TimelineId);

        // A Delay_Resp (HomePods send none) adds the measured path delay.
        long t3 = 1_100_000_000, t4 = t3 + offset + delay;
        var request = PtpPackets.Header(PtpPackets.DelayRequest, 44, Ours, 3, 0, 0x7f);
        observer.DelayRequestSent(3, t3);
        observer.ObserveCore(new(HomePod, 320), PtpPackets.DelayResponseMessage(HomePodClock, request, (ulong)t4), 320, t3 + 900_000);
        Assert.Equal(offset, observer.OffsetNs);
        Assert.Contains("GM 0x1122334455667788", observer.Describe(t2));
    }

    [Fact]
    public void A_one_step_Sync_is_a_sample_by_itself()
    {
        var observer = new PtpClockObserver(HomePod, Ours);
        var sync = PtpPackets.Header(PtpPackets.Sync, 44, HomePodClock, 9, 0, -3); // no two-step flag
        PtpPackets.WriteTimestamp(sync.AsSpan(PtpPackets.HeaderSize), (ulong)(7 * Second));
        observer.ObserveCore(new(HomePod, 319), sync, 319, 2 * Second);
        Assert.Equal(5 * Second, observer.OffsetNs);
        Assert.Equal(HomePodClock, observer.TimelineId); // no Announce: the Sync's clock
    }

    [Fact]
    public void Other_hosts_and_junk_are_counted_not_used()
    {
        var observer = new PtpClockObserver(HomePod, Ours);
        observer.ObserveCore(new(IPAddress.Parse("192.0.2.99"), 319), PtpPackets.SyncMessage(HomePodClock, 1), 319, 1);
        observer.ObserveCore(new(HomePod, 319), [1, 2, 3], 319, 2);
        observer.Observe(new(HomePod, 319), PtpPackets.SyncMessage(HomePodClock, 2)[..40], 319); // cut off: no sample, no throw
        Assert.Null(observer.OffsetNs);
        Assert.Null(observer.TimelineId);
        Assert.Contains("other hosts 1", observer.Describe(0));
        Assert.Contains("invalid 1", observer.Describe(0));

        var other = new PtpClockObserver(HomePod, Ours);
        other.ObserveCore(new(HomePod, 319), PtpPackets.SyncMessage(HomePodClock, 5), 319, 1);
        var badFraction = PtpPackets.FollowUpMessage(HomePodClock, 5, 0);
        badFraction[PtpPackets.HeaderSize + 6] = 0xff; // nanoseconds out of range
        other.Observe(new(HomePod, 320), badFraction, 320); // a FormatException inside: logged, never thrown
        Assert.Null(other.OffsetNs);
    }

    [Fact]
    public void Pending_Syncs_and_Delay_Reqs_are_evicted_oldest_first_across_the_sequence_wrap()
    {
        var observer = new PtpClockObserver(HomePod, Ours);
        // 106 two-step Syncs without Follow_Up, 65500 .. 65535 then 0 .. 69: only the last 64 (6 .. 69) stay pending.
        var sequences = Enumerable.Range(65500, 36).Concat(Enumerable.Range(0, 70)).Select(s => (ushort)s).ToArray();
        foreach (var (s, i) in sequences.Select((s, i) => (s, i)))
            observer.ObserveCore(new(HomePod, 319), PtpPackets.SyncMessage(HomePodClock, s), 319, (10 + i) * Second);
        observer.ObserveCore(new(HomePod, 320), PtpPackets.FollowUpMessage(HomePodClock, 65500, (ulong)(100 * Second)), 320, 0);
        observer.ObserveCore(new(HomePod, 320), PtpPackets.FollowUpMessage(HomePodClock, 5, (ulong)(100 * Second)), 320, 0);
        Assert.Null(observer.OffsetNs); // both evicted: the oldest before the wrap and after it
        observer.ObserveCore(new(HomePod, 320), PtpPackets.FollowUpMessage(HomePodClock, 69, (ulong)(200 * Second)), 320, 0);
        Assert.Equal((200 - (10 + 105)) * Second, observer.OffsetNs); // the newest, received at 115 s

        var delays = new PtpClockObserver(HomePod, Ours);
        delays.ObserveCore(new(HomePod, 319), PtpPackets.SyncMessage(HomePodClock, 1), 319, 10 * Second);
        delays.ObserveCore(new(HomePod, 320), PtpPackets.FollowUpMessage(HomePodClock, 1, (ulong)(14 * Second)), 320, 0);
        foreach (var (s, i) in sequences.Select((s, i) => (s, i))) delays.DelayRequestSent(s, i * Second);
        var oldest = PtpPackets.Header(PtpPackets.DelayRequest, 44, Ours, 65500, 0, 0x7f);
        delays.ObserveCore(new(HomePod, 320), PtpPackets.DelayResponseMessage(HomePodClock, oldest, (ulong)(10 * Second)), 320, 0);
        Assert.Equal(4 * Second, delays.OffsetNs); // evicted: no path delay
        var newest = PtpPackets.Header(PtpPackets.DelayRequest, 44, Ours, 69, 0, 0x7f);
        delays.ObserveCore(new(HomePod, 320), PtpPackets.DelayResponseMessage(HomePodClock, newest, (ulong)(109 * Second + 2_000)), 320, 0);
        Assert.Equal(4 * Second + 1_000, delays.OffsetNs); // ((t4 - t3) - oneWay) / 2 = 1 µs
    }

    [Fact]
    public void After_settling_a_silent_or_changed_clock_is_a_problem()
    {
        var observer = new PtpClockObserver(HomePod, Ours);
        observer.ObserveCore(new(HomePod, 320), PtpPackets.AnnounceMessage(HomePodClock, 1, 0, priority: 248), 320, 9 * Second);
        observer.ObserveCore(new(HomePod, 319), PtpPackets.SyncMessage(HomePodClock, 1), 319, 10 * Second);
        observer.ObserveCore(new(HomePod, 320), PtpPackets.FollowUpMessage(HomePodClock, 1, (ulong)(14 * Second)), 320, 10 * Second);
        Assert.Null(observer.ProblemAt(100 * Second, 30 * Second)); // not settled: nothing to lose yet
        observer.MarkSettled(10 * Second);
        Assert.Null(observer.ProblemAt(39 * Second, 30 * Second));
        Assert.Equal(L.T("HomePod 的 PTP 对时中断了"), observer.ProblemAt(41 * Second, 30 * Second));

        observer.ObserveCore(new(HomePod, 319), PtpPackets.SyncMessage(HomePodClock, 2), 319, 41 * Second);
        observer.ObserveCore(new(HomePod, 320), PtpPackets.FollowUpMessage(HomePodClock, 2, (ulong)(45 * Second)), 320, 41 * Second);
        Assert.Null(observer.ProblemAt(42 * Second, 30 * Second)); // Sync again

        observer.ObserveCore(new(HomePod, 320), PtpPackets.AnnounceMessage(0x99, 2, 0, priority: 248), 320, 42 * Second);
        Assert.Equal(L.T("HomePod 的 PTP 时钟变了"), observer.ProblemAt(42 * Second, 30 * Second));
    }

    [Fact]
    public async Task The_follower_settles_on_the_fake_grandmaster()
    {
        using var fake = new FakeAirPlayReceiver(appleTv: false);
        const long offset = 1_234 * Second;
        fake.StartGrandmaster(0xABCD, offset);
        using var follower = PtpFollower.Bind(IPAddress.Loopback, IPAddress.Loopback, Ours, fake.TimingPorts, TimeSpan.FromSeconds(30));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.True(await follower.SettleAsync(TimeSpan.FromSeconds(5), timeout.Token));
        Assert.Equal(0xABCDUL, follower.TimelineId);
        Assert.InRange(follower.OffsetNs!.Value, offset - 20_000_000, offset + 1_000_000); // loopback: well under 20 ms
        long now = Stopwatch.GetTimestamp();
        Assert.InRange((long)follower.TimeAt(now)!.Value - PtpClockObserver.LocalNs(now), offset - 20_000_000, offset + 1_000_000);
        Assert.Null(follower.Problem);
        Assert.InRange(follower.SyncAgeMs!.Value, 0, 2000);
        Assert.Contains("GM 0x000000000000ABCD", follower.Describe);
    }

    [Fact]
    public async Task No_grandmaster_means_no_settle()
    {
        using var fake = new FakeAirPlayReceiver(appleTv: false);
        using var follower = PtpFollower.Bind(IPAddress.Loopback, IPAddress.Loopback, Ours, fake.TimingPorts, TimeSpan.FromSeconds(30));
        Assert.False(await follower.SettleAsync(TimeSpan.FromMilliseconds(300), CancellationToken.None));
        Assert.Null(follower.TimelineId);
    }

    [Fact]
    public void Ports_held_by_another_program_refuse_5_1_after_retrying()
    {
        using var heldEvents = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var heldGeneral = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var ports = new PtpPorts(((IPEndPoint)heldEvents.Client.LocalEndPoint!).Port, ((IPEndPoint)heldGeneral.Client.LocalEndPoint!).Port, 9, 9);
        var watch = Stopwatch.StartNew();
        var error = Assert.Throws<SurroundUnavailableException>(() =>
            PtpFollower.Bind(IPAddress.Loopback, IPAddress.Loopback, Ours, ports, TimeSpan.FromSeconds(30)));
        Assert.Equal(L.T("UDP 端口 319 和 320 被其他程序占用"), error.Message);
        Assert.InRange(watch.Elapsed.TotalSeconds, 2.9, 10);
    }

    [Fact]
    public void The_same_ports_bind_again_after_Dispose()
    {
        int a, b;
        using (var probeA = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        using (var probeB = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            a = ((IPEndPoint)probeA.Client.LocalEndPoint!).Port;
            b = ((IPEndPoint)probeB.Client.LocalEndPoint!).Port;
        }
        var ports = new PtpPorts(a, b, 9, 9);
        var first = PtpFollower.Bind(IPAddress.Loopback, IPAddress.Loopback, Ours, ports, TimeSpan.FromSeconds(30));
        Assert.Equal(a, first.LocalEventPort);
        first.Dispose();
        var watch = Stopwatch.StartNew();
        using var second = PtpFollower.Bind(IPAddress.Loopback, IPAddress.Loopback, Ours, ports, TimeSpan.FromSeconds(30));
        Assert.Equal(b, second.LocalGeneralPort);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"rebind took {watch.Elapsed}");
    }
}
