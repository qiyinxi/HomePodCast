using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

/// <summary>StreamController on a group of fake speakers: no mDNS, no RTSP, no audio device.</summary>
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
