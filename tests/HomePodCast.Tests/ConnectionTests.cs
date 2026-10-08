using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class LatestRequestQueueTests
{
    [Fact]
    public async Task Requests_run_one_at_a_time_and_the_latest_waiting_one_wins()
    {
        var queue = new LatestRequestQueue("test");
        var ran = new ConcurrentQueue<string>();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int running = 0, overlap = 0;
        Action Step(string name, bool block = false) => () =>
        {
            if (Interlocked.Increment(ref running) > 1) overlap++;
            ran.Enqueue(name);
            if (block)
            {
                started.Set();
                release.Wait(5000);
            }
            Interlocked.Decrement(ref running);
        };

        _ = queue.Post(Step("connect", block: true));
        Assert.True(started.Wait(5000));
        var sw = Stopwatch.StartNew();
        _ = queue.Post(Step("disconnect"));          // replaced before it ran
        var idle = queue.Post(Step("connect again"));
        Assert.True(sw.ElapsedMilliseconds < 500, "posting waited for the running request");
        release.Set();
        await idle.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["connect", "connect again"], ran);
        Assert.Equal(0, overlap);

        // Idle again: the next request starts a new worker.
        await queue.Post(Step("disconnect")).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("disconnect", ran.Last());
    }

    [Fact]
    public async Task A_failing_request_does_not_stop_the_queue()
    {
        var queue = new LatestRequestQueue("test");
        bool ran = false;
        await queue.Post(() => throw new InvalidOperationException("boom")).WaitAsync(TimeSpan.FromSeconds(5));
        await queue.Post(() => ran = true).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(ran);
    }

    [Fact]
    public async Task Close_drops_what_waits_lets_the_running_one_finish_and_refuses_new_ones()
    {
        var queue = new LatestRequestQueue("test");
        var ran = new ConcurrentQueue<string>();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _ = queue.Post(() => { ran.Enqueue("stop"); started.Set(); release.Wait(5000); });
        Assert.True(started.Wait(5000));
        _ = queue.Post(() => ran.Enqueue("start"));

        var running = queue.Close();
        Assert.False(running.IsCompleted);
        _ = queue.Post(() => ran.Enqueue("late"));
        release.Set();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        Assert.Equal(["stop"], ran);
    }
}

/// <summary>
/// StreamController driven like the UI drives it (Connect/Disconnect through a LatestRequestQueue), on a group of
/// fake speakers: no mDNS, no RTSP, no audio device.
/// </summary>
public class ConnectionTests : IDisposable
{
    private readonly ConcurrentQueue<string> _log = new();
    private readonly List<FakeReceiver> _receivers = [];
    private readonly List<FakeMember> _members = [];
    private int _captures, _liveCaptures, _prepares;

    public void Dispose()
    {
        foreach (var rx in _receivers) rx.Dispose();
    }

    private sealed class FakeCapture(ConnectionTests owner, int stopMs) : ICaptureSource
    {
        private int _disposed;
        public string? DeviceName => "fake";
        public double DriftPpm => 0;
        public float Peak => 0;
        public int ExtraLatencyMs => 0;
        public event Action<string>? DeviceChanged { add { } remove { } }
        public void Start() { }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Thread.Sleep(stopMs); // RoutedCapture joins its threads and restores silenced apps
            Interlocked.Decrement(ref owner._liveCaptures);
        }
    }

    private static readonly GroupPlan Plan = new(GroupKind.MultiRoom, "A + B", null,
        [new GroupMemberRef("A", "A", null), new GroupMemberRef("B", "B", null)], false, false,
        new Dictionary<string, int> { ["B"] = -10 });

    private StreamController Controller(int captureStopMs = 0, Task? prepareGate = null)
    {
        var c = new StreamController
        {
            CaptureFactory = _ =>
            {
                Interlocked.Increment(ref _captures);
                Interlocked.Increment(ref _liveCaptures);
                return new FakeCapture(this, captureStopMs);
            },
            ResolveGroupMembers = (plan, _) => Task.FromResult(plan.Members
                .Select(m => new StreamController.ResolvedMember(m.DeviceId, m.Name, IPAddress.Loopback, 7000)).ToList()),
        };
        c.PrepareGroupMember = async (m, _, channels, ct) =>
        {
            Interlocked.Increment(ref _prepares); // the group's start volume is fixed by now
            if (prepareGate != null) await prepareGate.WaitAsync(ct);
            var rx = new FakeReceiver();
            var member = new FakeMember(m.DeviceId, rx, _log, channels);
            lock (_members)
            {
                _receivers.Add(rx);
                _members.Add(member);
            }
            return member;
        };
        return c;
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(what);
            await Task.Delay(10);
        }
    }

    private static double? Last(FakeMember m)
    {
        lock (m.Volumes) return m.Volumes.Count == 0 ? null : m.Volumes[^1];
    }

    private List<FakeMember> Members()
    {
        lock (_members) return [.. _members];
    }

    /// <summary>Speakers A and B of the latest connection (they are set up in parallel, in either order).</summary>
    private (FakeMember A, FakeMember B) Latest()
    {
        var all = Members();
        return (all.Last(m => m.Name == "A"), all.Last(m => m.Name == "B"));
    }

    [Fact]
    public async Task Connect_right_after_Disconnect_ends_connected_and_neither_waits_for_the_slow_stop()
    {
        var connection = new LatestRequestQueue("connection");
        using var c = Controller(captureStopMs: 400);
        await connection.Post(() => c.StartGroup(Plan, 150, 40)).WaitAsync(TimeSpan.FromSeconds(10));
        await Until(() => c.State == StreamState.Streaming, "streaming");

        var sw = Stopwatch.StartNew();
        _ = connection.Post(c.Stop);                                    // Disconnect: stopping takes 400 ms here
        var idle = connection.Post(() => c.StartGroup(Plan, 150, 40)); // Connect right after it
        Assert.True(sw.ElapsedMilliseconds < 200, $"the caller waited {sw.ElapsedMilliseconds} ms");
        await idle.WaitAsync(TimeSpan.FromSeconds(10));
        await Until(() => c.State == StreamState.Streaming, "streaming again");

        Assert.Equal(1, Volatile.Read(ref _liveCaptures));          // the stopped capture is gone, one runs
        Assert.Equal(2, Volatile.Read(ref _captures));
        Assert.NotNull(c.Capture);
        var members = Members();
        Assert.Equal(4, members.Count);
        Assert.True(members[0].Disposed && members[1].Disposed);
        Assert.False(members[2].Disposed || members[3].Disposed);
    }

    [Fact]
    public async Task Disconnect_right_after_Connect_leaves_nothing_running()
    {
        var connection = new LatestRequestQueue("connection");
        using var c = Controller(captureStopMs: 100);
        for (int round = 0; round < 5; round++)
        {
            _ = connection.Post(() => c.StartGroup(Plan, 150, 40));
            if (round % 2 == 1) await Task.Delay(30); // sometimes let the loop get as far as the capture
            await connection.Post(c.Stop).WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(50);                    // a loop Stop gave up on would start its capture by now

            Assert.Equal(StreamState.Idle, c.State);
            Assert.Null(c.Capture);
            Assert.Equal(0, Volatile.Read(ref _liveCaptures));
            Assert.All(Members(), m => Assert.True(m.Disposed));
        }
    }

    [Fact]
    public async Task A_volume_set_while_a_queued_connect_waits_is_kept()
    {
        using var c = Controller();
        int asOf = c.VolumeChanges;      // Connect reads the settings: 40 %
        c.SetVolume(70);                 // the user moves the slider before the queued start runs
        c.StartGroup(Plan, 150, 40, asOf);
        await Until(() => c.State == StreamState.Streaming, "streaming");
        Assert.Equal(70, c.Volume);
        var (a, b) = Latest();
        Assert.Equal(70, Last(a));
        Assert.Equal(60, Last(b));

        c.StartGroup(Plan, 150, 40, c.VolumeChanges); // nothing set since: the requested volume
        await Until(() => Members().Count == 4 && c.State == StreamState.Streaming, "reconnected");
        Assert.Equal(40, c.Volume);
    }

    [Fact]
    public async Task A_volume_mute_or_cap_change_made_while_the_group_connects_reaches_every_speaker()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var c = Controller(prepareGate: release.Task);
        c.StartGroup(Plan, 150, 40);
        await Until(() => Volatile.Read(ref _prepares) > 0, "setting up the speakers");
        c.SetVolume(70);                 // no group to send it to yet
        release.SetResult();
        await Until(() => c.State == StreamState.Streaming, "streaming");

        var (a, b) = Latest();
        await Until(() => Last(a) == 70 && Last(b) == 60, "the new volume on both speakers");
        lock (a.Volumes) Assert.Equal(40, a.Volumes[0]); // what the connect itself sent

        // The same for a lower cap and a mute while connecting.
        release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int before = Volatile.Read(ref _prepares);
        using var d = Controller(prepareGate: release.Task);
        d.StartGroup(Plan, 150, 80);
        await Until(() => Volatile.Read(ref _prepares) > before, "setting up the speakers");
        d.SetVolumeCap(50);
        d.SetMuted(true);
        release.SetResult();
        await Until(() => d.State == StreamState.Streaming, "streaming");
        (a, b) = Latest();
        await Until(() => Last(a) == 0 && Last(b) == 0, "muted on both speakers");
        d.SetMuted(false);
        await Until(() => Last(a) == 50 && Last(b) == 40, "the capped volume");
    }
}
