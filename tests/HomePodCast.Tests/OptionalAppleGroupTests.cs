using System.Collections.Concurrent;
using System.Net;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public class OptionalAppleGroupTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);
    private sealed class BusyOptional(string id) : ISpeakerBusyReader
    {
        public Task<BusyReading> ReadAsync(IReadOnlyList<BusyTarget> targets, CancellationToken ct) =>
            Task.FromResult(targets.Any(t => t.DeviceId == id)
                ? new BusyReading(SpeakerBusy.Busy, [id], "busy home theater") { Holder = BusyHolder.HomeTheater }
                : new BusyReading(SpeakerBusy.Free, [], "free"));
    }
    private sealed class Capture(AudioFifo fifo) : ICaptureSource
    {
        public string DeviceName => "fake";
        public string EndpointId => "capture";
        public double DriftPpm => 0;
        public float Peak => 0;
        public int ExtraLatencyMs => 0;
        public event Action<string>? DeviceChanged { add { } remove { } }
        public void Start() => fifo.Write(TestAudio.Signal(26460));
        public void Dispose() { }
    }
    private static AirPlayDevice Apple(string id, string address = "127.0.0.1", int port = 7000) =>
        new(id, id, IPAddress.Parse(address), port, "AudioAccessory6,1", new Dictionary<string, string>());
    private static GroupPlan Plan(params AirPlayDevice[] devices) => new(GroupKind.MultiRoom, "Apple group", null,
        devices.Select(d => new GroupMemberRef(d.DeviceId, d.Name, d.Address.ToString())).ToArray(), false, false,
        new Dictionary<string, int>());
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

    [Fact]
    public async Task Offline_optional_Apple_rejoins_with_fresh_address_and_one_UUID_without_restarting_primary()
    {
        var primary = Apple("aabb"); var optional = Apple("ccdd");
        using var primaryRx = new FakeReceiver(); using var optionalRx = new FakeReceiver();
        using var controller = Controller(primary, optional);
        int resolutions = 0, primaryPrepares = 0;
        var retry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new ConcurrentQueue<string>();
        var options = new ConcurrentQueue<(string Id, StreamOptions Options, ChannelMode Channels, IPAddress Address, int Port)>();
        controller.ResolveGroupReceiverDevice = (_, _) => Interlocked.Increment(ref resolutions) == 1
            ? Task.FromException<AirPlayDevice>(new IOException("offline"))
            : Task.FromResult(optional with { Address = IPAddress.Parse("127.0.0.2"), Port = 54321 });
        controller.RetryGroupReceiverOutput = (_, ct) => retry.Task.WaitAsync(ct);
        controller.PrepareGroupMember = (m, stream, channels, _) =>
        {
            options.Enqueue((m.DeviceId, stream, channels, m.Address, m.Port));
            if (m.DeviceId == primary.DeviceId) Interlocked.Increment(ref primaryPrepares);
            return Task.FromResult<IGroupMember>(new FakeMember(m.Label, m.DeviceId == primary.DeviceId ? primaryRx : optionalRx, log, channels));
        };
        controller.ConnectGroupReceiverOutput = (_, _, _, _, _, _) => throw new InvalidOperationException("Apple must not use third-party trials");
        controller.StartGroup(Plan(optional, primary) with
        {
            SplitChannels = true, RequiredOutputs = new Dictionary<string, bool> { ["CC:DD"] = false },
        }, 120, 10);
        await Until(() => controller.State == StreamState.Streaming);
        var sender = controller.ActiveSender;
        Assert.True(Assert.Single(controller.ReceiverGroupOutputs).Retrying);
        Assert.Equal(ChannelMode.RightOnly, Assert.Single(options).Channels);
        retry.TrySetResult();
        await Until(() => controller.ReceiverGroupOutputs.Single().State == OutputState.Streaming);
        Assert.Same(sender, controller.ActiveSender); Assert.Equal(1, primaryPrepares);
        var joined = options.Single(o => o.Id == optional.DeviceId);
        Assert.Equal(IPAddress.Parse("127.0.0.2"), joined.Address); Assert.Equal(54321, joined.Port);
        Assert.Equal(ChannelMode.Stereo, joined.Channels);
        Assert.Same(options.First().Options.SessionSetupExtras, joined.Options.SessionSetupExtras);
        Assert.Equal(optional.DeviceId, joined.Options.DeviceId);
        Assert.False(joined.Options.AllowMissingEventChannel); Assert.False(joined.Options.PairingOverRtsp);
        Assert.All(controller.GroupOutputStatuses, row => Assert.Equal(OutputState.Streaming, row.State));
    }

    [Fact]
    public async Task Unknown_offline_optional_identity_does_not_get_eagerly_resolved_or_become_required()
    {
        using var rx = new FakeReceiver();
        using var controller = Controller();
        var primary = Apple("aabb");
        controller.ReceiverLookup = id => id == primary.DeviceId ? primary : null;
        controller.ResolveGroupMembers = (plan, _) => Task.FromResult(plan.Members.Select(m =>
            new StreamController.ResolvedMember(m.DeviceId, m.Name, m.DeviceId == primary.DeviceId ? primary.Address : IPAddress.None, 7000)
                { Device = m.DeviceId == primary.DeviceId ? primary : null }).ToList());
        controller.ResolveGroupReceiverDevice = (_, _) => Task.FromException<AirPlayDevice>(new IOException("offline"));
        controller.RetryGroupReceiverOutput = (_, ct) => Task.Delay(Timeout.Infinite, ct);
        controller.PrepareGroupMember = (_, _, _, _) => Task.FromResult<IGroupMember>(new FakeMember("primary", rx, new()));
        // Optional has neither an address nor cached metadata; all resolution remains in fake delegates.
        var plan = Plan(primary) with
        {
            Members = [new(primary.DeviceId, primary.Name, "127.0.0.1"), new("ccdd", "offline", null)],
            RequiredOutputs = new Dictionary<string, bool> { ["CC-DD"] = false },
        };
        controller.StartGroup(plan, 120, 10);
        await Until(() => controller.State == StreamState.Streaming);
        var row = Assert.Single(controller.ReceiverGroupOutputs);
        Assert.False(row.Required); Assert.Equal(OutputState.Lost, row.State); Assert.True(row.Retrying);
    }

    [Theory]
    [InlineData(5000, 0)]
    [InlineData(null, -400)]
    public async Task All_classic_Apple_manual_delay_and_advanced_trim_use_coordinated_history(int? delay, int trim)
    {
        var primary = Apple("aabb"); var second = Apple("ccdd");
        using var firstRx = new FakeReceiver(); using var secondRx = new FakeReceiver();
        using var controller = Controller(primary, second);
        var streams = new ConcurrentQueue<StreamOptions>();
        controller.PrepareGroupMember = (m, options, channels, _) =>
        {
            streams.Enqueue(options);
            return Task.FromResult<IGroupMember>(new FakeMember(m.Label, m.DeviceId == primary.DeviceId ? firstRx : secondRx, new(), channels)
                { LatencyFrames = options.LatencyMs * 44100 / 1000 });
        };
        controller.StartGroup(Plan(primary, second) with { GroupDelayMs = delay,
            DelayTrims = new Dictionary<string, int> { [primary.DeviceId] = trim } }, 120, 10);
        await Until(() => controller.State == StreamState.Streaming);
        Assert.IsType<TimelineGroupSource>(controller.ActiveSender!.GroupSource);
        Assert.True(controller.EffectiveLatencyMs >= (delay ?? 120 + Math.Max(0, -trim) + TimelinePump.HeadroomMs));
        Assert.All(streams, s => Assert.True(s.LatencyMs <= controller.EffectiveLatencyMs - TimelinePump.HeadroomMs - Math.Max(0, -trim)));
    }

    [Fact]
    public async Task Optional_orderly_close_stays_terminal_and_does_not_retry_or_restart_primary()
    {
        var primary = Apple("aabb"); var optional = Apple("ccdd");
        using var firstRx = new FakeReceiver(); using var secondRx = new FakeReceiver();
        using var controller = Controller(primary, optional);
        FakeMember? optionalMember = null; int retryCalls = 0;
        controller.PrepareGroupMember = (m, _, channels, _) => Task.FromResult<IGroupMember>(m.DeviceId == optional.DeviceId
            ? optionalMember = new(m.Label, secondRx, new(), channels) : new FakeMember(m.Label, firstRx, new(), channels));
        controller.RetryGroupReceiverOutput = (_, ct) => { Interlocked.Increment(ref retryCalls); return Task.Delay(Timeout.Infinite, ct); };
        controller.StartGroup(Plan(primary, optional) with { RequiredOutputs = new Dictionary<string, bool> { [optional.DeviceId] = false } }, 120, 10);
        await Until(() => controller.State == StreamState.Streaming);
        var sender = controller.ActiveSender;
        optionalMember!.Lose(EventChannel.ClosedBySpeaker);
        await Until(() => controller.ReceiverGroupOutputs.Single().NeedsUserAction);
        Assert.Equal(0, retryCalls); Assert.Same(sender, controller.ActiveSender);
        Assert.Equal(StreamState.Streaming, controller.State);
    }

    [Fact]
    public async Task Automatic_optional_busy_home_theater_is_terminal_without_reclaim_or_retry()
    {
        var primary = Apple("aabb"); var optional = Apple("ccdd");
        using var rx = new FakeReceiver(); using var controller = Controller(primary, optional);
        int optionalPrepares = 0, retries = 0;
        controller.BusyReader = new BusyOptional(optional.DeviceId); controller.HomeTheaterMode = HomeTheaterMode.TakeOver;
        controller.PrepareGroupMember = (m, _, _, _) =>
        {
            if (m.DeviceId == optional.DeviceId) Interlocked.Increment(ref optionalPrepares);
            return Task.FromResult<IGroupMember>(new FakeMember(m.Label, rx, new()));
        };
        controller.RetryGroupReceiverOutput = (_, ct) => { Interlocked.Increment(ref retries); return Task.Delay(Timeout.Infinite, ct); };
        controller.StartGroup(Plan(primary, optional) with { RequiredOutputs = new Dictionary<string, bool> { [optional.DeviceId] = false } },
            120, 10, trigger: ConnectTrigger.Automatic);
        await Until(() => controller.State == StreamState.Streaming);
        var row = Assert.Single(controller.ReceiverGroupOutputs);
        Assert.True(row.NeedsUserAction); Assert.False(row.Retrying);
        Assert.Equal(0, optionalPrepares); Assert.Equal(0, retries);
    }

    [Fact]
    public async Task Current_cap_and_mute_apply_when_optional_Apple_prepare_finishes()
    {
        var primary = Apple("aabb"); var optional = Apple("ccdd");
        using var firstRx = new FakeReceiver(); using var secondRx = new FakeReceiver();
        using var controller = Controller(primary, optional);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeMember? optionalMember = null, primaryMember = null;
        controller.PrepareGroupMember = async (m, _, channels, ct) =>
        {
            if (m.DeviceId == primary.DeviceId) return primaryMember = new(m.Label, firstRx, new(), channels);
            entered.TrySetResult(); await release.Task.WaitAsync(ct);
            return optionalMember = new(m.Label, secondRx, new(), channels);
        };
        controller.StartGroup(Plan(primary, optional) with { RequiredOutputs = new Dictionary<string, bool> { [optional.DeviceId] = false } }, 120, 80);
        await entered.Task.WaitAsync(Guard);
        controller.SetVolumeCap(15); controller.SetMuted(true); release.TrySetResult();
        await Until(() => controller.State == StreamState.Streaming);
        Assert.All(optionalMember!.Volumes, volume => Assert.Equal(0, volume));
        Assert.All(primaryMember!.Volumes, volume => Assert.Equal(0, volume));
    }

    [Fact]
    public void Empty_selection_never_starts_capture()
    {
        using var controller = Controller();
        controller.CaptureFactory = _ => throw new InvalidOperationException("empty selection opened capture");
        controller.StartGroup(Plan(), 120, 10);
        Assert.Equal(StreamState.Idle, controller.State); Assert.Null(controller.Capture);
    }
}
