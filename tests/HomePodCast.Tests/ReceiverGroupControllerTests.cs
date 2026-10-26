using System.Collections.Concurrent;
using System.Net;
using HomePodCast.Audio;
using HomePodCast.Net;
using Transport = HomePodCast.Tests.ReceiverOutputSetTests.Transport;

namespace HomePodCast.Tests;

public class ReceiverGroupControllerTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);
    private sealed class Capture(AudioFifo fifo) : ICaptureSource
    {
        public int Disposals;
        public string DeviceName => "fake";
        public string EndpointId => "capture";
        public double DriftPpm => 0;
        public float Peak => 0;
        public int ExtraLatencyMs => 0;
        public event Action<string>? DeviceChanged { add { } remove { } }
        public void Start() => fifo.Write(TestAudio.Signal(26460));
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }
    private static AirPlayDevice Receiver(string id = "aabb", int port = 7001) => new("Software", id, IPAddress.Loopback,
        port, "ThirdParty", new Dictionary<string, string> { ["features"] = "0x0,0x300" });
    private static AirPlayDevice Home() => new("HomePod", "ccdd", IPAddress.Loopback, 7100, "AudioAccessory6,1", new Dictionary<string, string>());
    private static GroupPlan Plan(params AirPlayDevice[] devices) => new(GroupKind.MultiRoom, "Mixed", null,
        devices.Select(d => new GroupMemberRef(d.DeviceId, d.Name, "127.0.0.1")).ToArray(), false, false, new Dictionary<string, int>());
    private static StreamController Controller(params AirPlayDevice[] devices) => new()
    {
        CaptureFactory = fifo => new Capture(fifo), CapturedOutputId = () => "capture",
        ReceiverLookup = id => devices.FirstOrDefault(d => GroupPlan.Key(d.DeviceId) == GroupPlan.Key(id)),
        ResolveGroupMembers = (plan, _) => Task.FromResult(plan.Members.Select(m =>
        {
            var d = devices.Single(d => GroupPlan.Key(d.DeviceId) == GroupPlan.Key(m.DeviceId));
            return new StreamController.ResolvedMember(d.DeviceId, d.Name, d.Address, d.Port) { Device = d };
        }).ToList()),
    };
    private static async Task Until(Func<bool> condition)
    {
        using var stop = new CancellationTokenSource(Guard);
        while (!condition()) await Task.Delay(5, stop.Token);
    }

    [Theory]
    [InlineData(ReceiverProtocol.AirPlay2Realtime, 250)]
    [InlineData(ReceiverProtocol.AirPlay2Buffered, 1500)]
    public async Task Mixed_transports_share_the_future_epoch_preserve_channel_indexes_and_negotiate_delay(ReceiverProtocol protocol, int latency)
    {
        var device = Receiver(); var home = Home();
        using var rx = new FakeReceiver();
        var transport = new Transport(latency, protocol);
        using var controller = Controller(device, home);
        int captures = 0;
        controller.CaptureFactory = fifo => { captures++; return new Capture(fifo); };
        controller.SetVolumeCap(25);
        ChannelMode? homeChannel = null;
        controller.PrepareGroupMember = (m, _, channel, _) =>
        {
            homeChannel = channel;
            return Task.FromResult<IGroupMember>(new FakeMember(m.Label, rx, new ConcurrentQueue<string>(), channel));
        };
        controller.ConnectGroupReceiverOutput = (_, _, _, _, _, _) => Task.FromResult<IPreparedReceiverSession>(transport);
        controller.StartGroup(Plan(device, home) with { SplitChannels = true }, 120, 80);
        await Until(() => controller.State == StreamState.Streaming);
        Assert.Equal(ChannelMode.RightOnly, homeChannel);
        Assert.Equal(controller.ActiveSender!.T0, transport.StartAt);
        Assert.True(controller.EffectiveLatencyMs >= latency + TimelinePump.HeadroomMs);
        Assert.Equal(OutputState.Streaming, Assert.Single(controller.ReceiverGroupOutputs).State);
        Assert.All(transport.Volumes, volume => Assert.InRange(volume, 0, 25));
        Assert.Equal(1, captures); Assert.Equal(1, transport.Starts);
    }

    [Fact]
    public async Task A_third_party_only_group_needs_no_HomePod_sender_and_stops_capture()
    {
        var transport = new Transport(1500, ReceiverProtocol.AirPlay2Buffered);
        using var controller = Controller(Receiver());
        Capture? capture = null;
        controller.CaptureFactory = fifo => capture = new Capture(fifo);
        controller.ConnectGroupReceiverOutput = (_, _, _, _, _, _) => Task.FromResult<IPreparedReceiverSession>(transport);
        controller.StartGroup(Plan(Receiver()), 120, 10);
        await Until(() => controller.State == StreamState.Streaming);
        Assert.Null(controller.ActiveSender); Assert.Null(controller.Client);
        Assert.Equal(1520, controller.EffectiveLatencyMs);
        controller.Stop();
        Assert.Equal(1, capture!.Disposals);
        Assert.True(transport.Stops > 0);
    }

    [Fact]
    public async Task Manual_addresses_keep_their_explicit_port_and_publish_the_working_group_protocol()
    {
        var device = Receiver();
        var explicitDevice = device with { Port = 54321 };
        var transport = new Transport(1500, ReceiverProtocol.AirPlay2Buffered);
        using var controller = Controller(device);
        controller.ResolveGroupMembers = null;
        controller.ManualGroupReceiverDevice = _ => explicitDevice;
        int? connectedPort = null;
        (string Id, ReceiverProtocol Protocol, int? Processing)? remembered = null;
        controller.ConnectGroupReceiverOutput = (_, target, _, _, _, _) =>
        {
            connectedPort = target.Port;
            return Task.FromResult<IPreparedReceiverSession>(transport);
        };
        controller.ReceiverProtocolConnected += (id, protocol, processing) => remembered = (id, protocol, processing);
        controller.StartGroup(Plan(device), 120, 10);
        await Until(() => controller.State == StreamState.Streaming);
        Assert.Equal(54321, connectedPort);
        Assert.Equal((device.DeviceId, ReceiverProtocol.AirPlay2Buffered, (int?)100), remembered);
    }

    [Fact]
    public async Task A_mute_during_HomePod_prepare_is_applied_before_the_receiver_audio_gate_opens()
    {
        var transport = new Transport();
        using var rx = new FakeReceiver();
        using var controller = Controller(Receiver(), Home());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.ConnectGroupReceiverOutput = (_, _, _, _, _, _) => Task.FromResult<IPreparedReceiverSession>(transport);
        controller.PrepareGroupMember = async (_, _, _, ct) =>
        {
            entered.TrySetResult(); await release.Task.WaitAsync(ct);
            return new FakeMember("HomePod", rx, new ConcurrentQueue<string>());
        };
        controller.StartGroup(Plan(Receiver(), Home()), 120, 80);
        await entered.Task.WaitAsync(Guard);
        Assert.Equal(0, transport.Starts);
        controller.SetMuted(true); release.TrySetResult();
        await Until(() => controller.State == StreamState.Streaming);
        Assert.All(transport.Volumes, volume => Assert.Equal(0, volume));
        float[] pcm = new float[704]; transport.Source!.Read(transport.StartAt, pcm);
        Assert.All(pcm, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task Required_receiver_takeover_cancels_HomePod_prepare_and_preserves_the_terminal_reason()
    {
        var transport = new Transport();
        using var controller = Controller(Receiver(), Home());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.ConnectGroupReceiverOutput = (_, _, _, _, _, _) => Task.FromResult<IPreparedReceiverSession>(transport);
        controller.PrepareGroupMember = async (_, _, _, ct) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { cancelled.TrySetResult(); }
            throw new InvalidOperationException("cancelled prepare must not return");
        };
        controller.StartGroup(Plan(Receiver(), Home()), 120, 10);
        await entered.Task.WaitAsync(Guard);
        transport.Lose(new ReceiverGroupStoppedException("taken over"));
        await cancelled.Task.WaitAsync(Guard);
        await Until(() => controller.State == StreamState.Idle);
        Assert.Contains("taken over", controller.StatusText);
        Assert.Equal(0, transport.Starts); Assert.Null(controller.Capture);
    }

    [Fact]
    public async Task Required_receiver_takeover_releases_the_capture_before_Idle_is_published()
    {
        // 2026-10-26: the group loop published Idle and only then disposed the capture, so whoever saw Idle first (a
        // Changed handler, the tray's monitor guard, the test above polling State) could still find the capture.
        var transport = new Transport();
        using var controller = Controller(Receiver(), Home());
        Capture? capture = null;
        controller.CaptureFactory = fifo => capture = new Capture(fifo);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.ConnectGroupReceiverOutput = (_, _, _, _, _, _) => Task.FromResult<IPreparedReceiverSession>(transport);
        controller.PrepareGroupMember = async (_, _, _, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("cancelled prepare must not return");
        };
        // Changed runs on the loop inside Set: what it reads is exactly what Idle was published with.
        var atIdle = new TaskCompletionSource<(ICaptureSource? Capture, int Disposals)>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Changed += () =>
        {
            if (controller.State == StreamState.Idle && controller.StatusText.Contains("taken over"))
                atIdle.TrySetResult((controller.Capture, Volatile.Read(ref capture!.Disposals)));
        };
        controller.StartGroup(Plan(Receiver(), Home()), 120, 10);
        await entered.Task.WaitAsync(Guard);
        Assert.NotNull(controller.Capture);
        transport.Lose(new ReceiverGroupStoppedException("taken over"));
        var seen = await atIdle.Task.WaitAsync(Guard);
        Assert.Null(seen.Capture);
        Assert.Equal(1, seen.Disposals);
    }

    [Fact]
    public async Task Optional_busy_receiver_leaves_the_HomePod_streaming_and_expires_its_interaction_on_stop()
    {
        using var rx = new FakeReceiver();
        using var controller = Controller(Receiver(), Home());
        controller.PrepareGroupMember = (_, _, _, _) => Task.FromResult<IGroupMember>(new FakeMember("HomePod", rx, new ConcurrentQueue<string>()));
        controller.ConnectGroupReceiverOutput = (_, _, _, _, _, _) => Task.FromException<IPreparedReceiverSession>(new RaopBusyException());
        GroupReceiverInteraction? request = null;
        controller.GroupReceiverActionRequired += value => request = value;
        controller.StartGroup(Plan(Receiver(), Home()) with { RequiredOutputs = new Dictionary<string, bool> { ["aabb"] = false } }, 120, 10);
        await Until(() => controller.State == StreamState.Streaming);
        Assert.True(Assert.Single(controller.ReceiverGroupOutputs).NeedsUserAction);
        Assert.NotNull(request); Assert.True(controller.MatchesGroupReceiverInteraction(request));
        controller.Stop();
        Assert.False(controller.MatchesGroupReceiverInteraction(request));
    }
}
