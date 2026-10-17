using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

/// <summary>
/// The wireless cinema's hold on the StreamController (CinemaSession), driven as TrayApp drives it, on fake speakers:
/// no mDNS, no RTSP, no audio device. The cinema's speakers are C1 and C2, the user's own group A + B.
/// </summary>
public class CinemaSessionTests : IDisposable
{
    private readonly ConcurrentQueue<string> _log = new();
    private readonly List<FakeReceiver> _receivers = [];
    private readonly List<FakeMember> _members = [];
    private int _captures, _cinemaPrepares;

    public void Dispose()
    {
        foreach (var rx in _receivers) rx.Dispose();
    }

    private static readonly GroupPlan Own = new(GroupKind.MultiRoom, "A + B", null,
        [new GroupMemberRef("A", "A", null), new GroupMemberRef("B", "B", null)], false, false, new Dictionary<string, int>());

    private static readonly CinemaSpeaker[] Speakers =
    [
        new() { Role = CinemaRole.FrontLeft, DeviceId = "C1", Name = "C1" },
        new() { Role = CinemaRole.FrontRight, DeviceId = "C2", Name = "C2" },
    ];

    private sealed class FakeSource : IGroupPacketSource
    {
        private int _disposed;
        public bool Disposed => Volatile.Read(ref _disposed) != 0;
        public bool ReadPacket(long ticks) => true;
        public void ReadTarget(int index, Span<float> stereo) => stereo.Fill(0.01f);
        public void SkipPackets(int packets) { }
        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }

    private sealed class FakeCapture : ICaptureSource
    {
        public string? DeviceName => "fake";
        public double DriftPpm => 0;
        public float Peak => 0;
        public int ExtraLatencyMs => 0;
        public event Action<string>? DeviceChanged { add { } remove { } }
        public void Start() { }
        public void Dispose() { }
    }

    /// <param name="cinemaGate">The cinema's speakers finish connecting only once this completes (null: at once).</param>
    private StreamController Controller(Task? cinemaGate = null)
    {
        var c = new StreamController
        {
            CaptureFactory = _ =>
            {
                Interlocked.Increment(ref _captures);
                return new FakeCapture();
            },
            ResolveGroupMembers = (plan, _) => Task.FromResult(plan.Members
                .Select(m => new StreamController.ResolvedMember(m.DeviceId, m.Name, IPAddress.Loopback, 7000)).ToList()),
        };
        c.PrepareGroupMember = async (m, _, channels, ct) =>
        {
            if (m.DeviceId.StartsWith('C'))
            {
                Interlocked.Increment(ref _cinemaPrepares);
                if (cinemaGate != null) await cinemaGate.WaitAsync(ct);
            }
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

    private List<FakeMember> Members(string id)
    {
        lock (_members) return _members.Where(m => m.Name == id).ToList();
    }

    private static double? Last(FakeMember m)
    {
        lock (m.Volumes) return m.Volumes.Count == 0 ? null : m.Volumes[^1];
    }

    private static async Task OwnStreaming(StreamController c, LatestRequestQueue connection, double volume)
    {
        await connection.Post(() => c.StartGroup(Own, 150, volume)).WaitAsync(TimeSpan.FromSeconds(10));
        await Until(() => c.State == StreamState.Streaming, "the user's own group streaming");
    }

    [Fact]
    public async Task The_cinema_starts_at_the_windows_volume_and_keeps_one_set_while_its_start_waits()
    {
        var connection = new LatestRequestQueue("connection");
        using var c = Controller();
        var session = new CinemaSession(c, connection);
        await OwnStreaming(c, connection, 60); // the saved volume: 60 %
        Assert.Equal(1, _captures);

        // The window's slider says 10 %: the cinema starts there, not at the saved 60 % (review of #22).
        var first = new FakeSource();
        session.Start(first, Speakers, 200, 10);
        await Until(() => session.StateOf(first) == CinemaState.Playing, "the cinema playing");
        Assert.Same(first, c.GroupSource);
        Assert.Equal(10, c.Volume);
        Assert.All(Members("C1").Concat(Members("C2")), m => Assert.Equal(10, Last(m)));
        Assert.Equal(1, _captures);                // the file plays: no system capture
        Assert.Null(c.ActiveSender?.Effects);      // and no EQ or night compressor

        // A volume set while the next start waits in the queue (the slider moved again, a volume key) is kept.
        using var release = new ManualResetEventSlim();
        using var busy = new ManualResetEventSlim();
        _ = connection.Post(() => { busy.Set(); release.Wait(5000); }); // the queue is busy (a slow Stop)
        Assert.True(busy.Wait(5000));
        var second = new FakeSource();
        session.Start(second, Speakers, 200, 10);
        c.SetVolume(35);
        release.Set();
        await Until(() => session.StateOf(second) == CinemaState.Playing, "the second file playing");
        Assert.Equal(35, c.Volume);
        Assert.Equal(35, Last(Members("C1")[^1]));
        Assert.True(first.Disposed);
        Assert.Equal(CinemaState.Ended, session.StateOf(first));
    }

    [Fact]
    public async Task A_connection_made_while_the_cinema_waits_ends_it_and_the_windows_stop_leaves_it_alone()
    {
        var connection = new LatestRequestQueue("connection");
        var never = new TaskCompletionSource();
        using var c = Controller(cinemaGate: never.Task);
        var session = new CinemaSession(c, connection);
        var source = new FakeSource();
        session.Start(source, Speakers, 200, 20);
        await Until(() => Volatile.Read(ref _cinemaPrepares) > 0, "the cinema connecting");
        Assert.Equal(CinemaState.Waiting, session.StateOf(source));

        // 连接 on 首页 or the tray (TrayApp.PostConnection): the cinema is superseded, the user's group streams.
        Assert.True(session.Supersede());
        await connection.Post(() => c.StartGroup(Own, 150, 40)).WaitAsync(TimeSpan.FromSeconds(10));
        await Until(() => c.State == StreamState.Streaming, "the user's group streaming");
        Assert.Equal(CinemaState.Ended, session.StateOf(source));
        Assert.Null(c.GroupSource);
        Assert.True(source.Disposed);

        // The window notices and stops its playback: the user's stream goes on.
        Assert.True(session.Stop(source));
        await connection.Post(() => { }).WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(100);
        Assert.Equal(StreamState.Streaming, c.State);
        Assert.All(Members("A").Concat(Members("B")), m => Assert.False(m.Disposed));
        Assert.False(session.Supersede()); // nothing of the cinema's left
    }

    [Fact]
    public async Task Stopping_a_cinema_start_still_queued_drops_it_and_leaves_what_plays()
    {
        var connection = new LatestRequestQueue("connection");
        using var c = Controller();
        var session = new CinemaSession(c, connection);
        await OwnStreaming(c, connection, 40);

        using var release = new ManualResetEventSlim();
        using var busy = new ManualResetEventSlim();
        _ = connection.Post(() => { busy.Set(); release.Wait(5000); }); // the queue is busy (a slow Stop)
        Assert.True(busy.Wait(5000));
        var source = new FakeSource();
        session.Start(source, Speakers, 200, 20);
        Assert.Equal(CinemaState.Waiting, session.StateOf(source));
        Assert.False(session.Stop(source)); // it never reached the controller
        release.Set();
        await connection.Post(() => { }).WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(100);

        Assert.Equal(0, Volatile.Read(ref _cinemaPrepares));
        Assert.Equal(StreamState.Streaming, c.State);
        Assert.Null(c.GroupSource);
        Assert.True(source.Disposed);
        Assert.Equal(CinemaState.Ended, session.StateOf(source));
    }

    [Fact]
    public async Task Stopping_the_cinema_stops_the_controller_and_disposes_its_file()
    {
        var connection = new LatestRequestQueue("connection");
        using var c = Controller();
        var session = new CinemaSession(c, connection);
        var source = new FakeSource();
        session.Start(source, Speakers, 200, 20);
        await Until(() => session.StateOf(source) == CinemaState.Playing, "the cinema playing");

        Assert.True(session.Stop(source));
        await Until(() => c.State == StreamState.Idle && c.GroupSource == null, "the controller stopped");
        Assert.True(source.Disposed);
        Assert.All(Members("C1").Concat(Members("C2")), m => Assert.True(m.Disposed));
        Assert.Equal(CinemaState.Ended, session.StateOf(source));
    }

    [Fact]
    public async Task Another_sender_taking_a_speaker_ends_the_cinema_but_the_moment_before_it_connects_does_not()
    {
        var connection = new LatestRequestQueue("connection");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var c = Controller(cinemaGate: gate.Task);
        c.TakeoverGrace = TimeSpan.Zero;
        var session = new CinemaSession(c, connection);
        var source = new FakeSource();
        session.Start(source, Speakers, 200, 20);
        // Published to the controller, and Idle or connecting: still the cinema's, waiting.
        await Until(() => c.GroupSource == source, "the start running");
        Assert.Equal(CinemaState.Waiting, session.StateOf(source));
        gate.SetResult();
        await Until(() => session.StateOf(source) == CinemaState.Playing, "the cinema playing");

        Members("C1")[^1].Lose(EventChannel.ClosedBySpeaker); // an iPhone took C1
        await Until(() => c.State == StreamState.Idle, "the controller giving up");
        Assert.Equal(CinemaState.Ended, session.StateOf(source));
    }

    [Fact]
    public async Task A_measurement_caps_the_speakers_at_25_percent_and_gives_the_volume_back()
    {
        var connection = new LatestRequestQueue("connection");
        using var c = Controller();
        var session = new CinemaSession(c, connection);
        var source = new FakeSource();
        session.Start(source, Speakers, 200, 60);
        await Until(() => session.StateOf(source) == CinemaState.Playing, "the cinema playing");
        var left = Members("C1")[^1];
        await Until(() => Last(left) == 60, "60 % on the speaker");

        c.SetMeasurementCap(AcousticAlignment.MeasurementVolumeCapPercent);
        await c.ConfirmGroupMeasurementVolumeAsync(default);
        await Until(() => Last(left) == 25, "the measurement's 25 %");
        Assert.Equal(60, c.Volume); // what the app shows and saves is unchanged
        c.SetVolume(40);           // a change meanwhile stays under the ceiling too
        await Task.Delay(50);
        Assert.Equal(25, Last(left));

        c.EndMeasurementCap();
        await Until(() => Last(left) == 40, "the volume back");
        c.SetMeasurementCap(25);
        c.Stop();
        Assert.Null(c.MeasurementCapPercent); // never outlives its stream
    }
}
