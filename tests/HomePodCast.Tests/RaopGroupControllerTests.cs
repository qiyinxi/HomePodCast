using System.Collections.Concurrent;
using System.Net;
using HomePodCast.Audio;
using HomePodCast.Net;
using FakeRaop = HomePodCast.Tests.RaopOutputSetTests.FakeGroupRaop;

namespace HomePodCast.Tests;

public class RaopGroupControllerTests
{
    private sealed class Capture : ICaptureSource
    {
        public int Disposals;
        public string DeviceName => "fake";
        public string EndpointId => "capture";
        public double DriftPpm => 0;
        public float Peak => 0;
        public int ExtraLatencyMs => 0;
        public event Action<string>? DeviceChanged { add { } remove { } }
        public void Start() { }
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }

    private sealed class Source : IGroupPacketSource
    {
        public int Disposals;
        public bool ReadPacket(long ticks) => true;
        public void ReadTarget(int index, Span<float> stereo) => stereo.Clear();
        public void SkipPackets(int packets) { }
        public void Dispose() => Disposals++;
    }

    private sealed class BusyReader : ISpeakerBusyReader
    {
        public volatile bool RaopBusy;
        public ConcurrentQueue<BusyTarget[]> Reads { get; } = new();
        public Task<BusyReading> ReadAsync(IReadOnlyList<BusyTarget> targets, CancellationToken ct)
        {
            Reads.Enqueue(targets.ToArray());
            return Task.FromResult(BusyReading.Combine(targets.Select(t => new BusyReading.Member(t.Name,
                RaopBusy && GroupPlan.Key(t.DeviceId) == "AABB" ? SpeakerBusy.Busy : SpeakerBusy.Free, t.Name)).ToArray()));
        }
    }

    private static AirPlayDevice Raop(int port) => new("RAOP", "AA:BB", IPAddress.Loopback, 7000, "AirPort10,115", new Dictionary<string, string>())
    {
        HasAirPlay = false, RaopPort = port, RaopTxt = new Dictionary<string, string> { ["et"] = "0", ["cn"] = "1" },
    };

    private static AirPlayDevice Ap2() => new("HomePod", "CC:DD", IPAddress.Loopback, 7100, "AudioAccessory5,1", new Dictionary<string, string>());

    private static GroupPlan Plan(params AirPlayDevice[] devices) => new(GroupKind.MultiRoom, "Mixed", null,
        devices.Select(d => new GroupMemberRef(d.DeviceId, d.Name, d.Address.ToString())).ToArray(), false, false, new Dictionary<string, int>());

    private static StreamController Controller(Capture capture, params AirPlayDevice[] devices) => new()
    {
        CaptureFactory = _ => capture, CapturedOutputId = () => "capture",
        ReceiverLookup = id => devices.FirstOrDefault(d => GroupPlan.Key(d.DeviceId) == GroupPlan.Key(id)),
        ResolveGroupMembers = (plan, _) => Task.FromResult(plan.Members.Select(m =>
        {
            var device = devices.Single(d => GroupPlan.Key(d.DeviceId) == GroupPlan.Key(m.DeviceId));
            return new StreamController.ResolvedMember(m.DeviceId, m.Name, device.Address, device.Port);
        }).ToList()),
    };

    private static async Task Until(Func<bool> condition)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(5, limit.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_receiver_discovered_after_start_still_disposes_an_unsupported_file_source_once(bool thirdParty)
    {
        var device = thirdParty
            ? new AirPlayDevice("Third party", "EE:FF", IPAddress.Loopback, 12345, "Unknown", new Dictionary<string, string>())
            : Raop(12345);
        var capture = new Capture();
        var source = new Source();
        using var controller = Controller(capture);
        controller.ReceiverLookup = _ => null;
        controller.ResolveGroupMembers = (_, _) => Task.FromResult(new List<StreamController.ResolvedMember>
        {
            new(device.DeviceId, device.Name, device.Address, device.Port) { Device = device },
        });
        controller.PrepareGroupMember = (_, _, _, _) => throw new InvalidOperationException("must refuse before connecting");
        controller.ConnectGroupRaopOutput = (_, _, _) => throw new InvalidOperationException("must refuse before connecting");
        controller.StartGroup(Plan(device), 120, 10, source: source);
        await Until(() => controller.State == StreamState.Idle && source.Disposals == 1);
        Assert.Null(controller.GroupSource);
        Assert.Null(controller.Capture);
        controller.Stop();
        Assert.Equal(1, source.Disposals);
    }

    [Theory]
    [InlineData(1000, 1000)]
    [InlineData(1500, 1500)]
    [InlineData(2000, 2000)]
    [InlineData(2020, 2000)]
    public async Task Saved_metadata_resolves_RAOP_without_fixed_7000_and_preserves_port_TXT_password_and_latency(int selected, int expected)
    {
        using var fake = new FakeRaop();
        var device = Raop(fake.Port);
        var preferences = ReceiverPreferences.Of(device);
        var capture = new Capture();
        var busy = new BusyReader();
        using var controller = Controller(capture, device);
        controller.ReceiverLookup = _ => null;
        controller.ReceiverSettings = _ => preferences;
        controller.ResolveGroupMembers = null; // Exercise the production resolver on the advertised RAOP port.
        controller.BusyReader = busy;
        controller.CompatibilityLatency = _ => selected;
        controller.ReceiverPassword = _ => "stored password";
        controller.PrepareGroupMember = (_, _, _, _) => throw new InvalidOperationException("RAOP must not prepare AP2");
        RaopGroupOutputOptions? output = null;
        RaopOptions? transport = null;
        controller.ConnectGroupRaopOutput = (o, options, ct) =>
        {
            output = o; transport = options;
            return RaopClient.ConnectAsync(o.Address, o.Port, options, ct);
        };
        controller.StartGroup(Plan(device) with { GroupDelayMs = 3000 }, 120, 10);
        await Until(() => controller.State == StreamState.Streaming);
        Assert.Equal(fake.Port, output!.Port);
        Assert.Same(preferences.RaopTxt, transport!.Txt);
        Assert.Equal("stored password", transport.Password);
        Assert.Equal(expected, transport.LatencyMs);
        Assert.Equal(3000, controller.EffectiveLatencyMs);
        Assert.Null(controller.ActiveSender);
        Assert.Equal(OutputState.Streaming, Assert.Single(controller.RaopGroupOutputs).State);
        Assert.All(busy.Reads.SelectMany(r => r), target => Assert.Equal(fake.Port, target.Port));
        await fake.Audio.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        controller.Stop();
        Assert.Equal(1, capture.Disposals);
    }

    [Fact]
    public async Task Filtering_RAOP_preserves_original_split_channel_indexes_and_caps_AP2_transport_buffer()
    {
        using var fake = new FakeRaop();
        using var receiver = new FakeReceiver();
        var raop = Raop(fake.Port);
        var ap2 = Ap2();
        var capture = new Capture();
        var local = new FakeLocalRenderDevice();
        using var controller = Controller(capture, raop, ap2);
        controller.OpenLocalOutput = _ => local;
        ChannelMode? ap2Channels = null, raopChannels = null;
        StreamOptions? ap2Options = null;
        controller.PrepareGroupMember = (m, options, channels, _) =>
        {
            ap2Channels = channels; ap2Options = options;
            return Task.FromResult<IGroupMember>(new FakeMember(m.Label, receiver, new(), channels));
        };
        controller.ConnectGroupRaopOutput = (o, options, ct) =>
        {
            raopChannels = o.Channels;
            return RaopClient.ConnectAsync(o.Address, o.Port, options, ct);
        };
        controller.StartGroup(Plan(raop, ap2) with
        {
            SplitChannels = true, GroupDelayMs = 30000, LocalOutputs = [new("local", "Local")],
        }, 120, 10);
        await Until(() => controller.State == StreamState.Streaming);
        Assert.Equal(ChannelMode.LeftOnly, raopChannels);
        Assert.Equal(ChannelMode.RightOnly, ap2Channels);
        Assert.Equal(30000, controller.EffectiveLatencyMs);
        Assert.Equal(2000, ap2Options!.LatencyMs);
        Assert.NotNull(controller.ActiveSender);
        Assert.Single(controller.LocalGroupOutputs);
        controller.Stop();
        Assert.True(local.Disposed);
    }

    [Fact]
    public async Task Capture_feedback_is_refused_before_resolving_or_connecting_RAOP()
    {
        var device = Raop(5678);
        int resolves = 0, connects = 0;
        using var controller = Controller(new Capture(), device);
        controller.ResolveGroupMembers = (_, _) => { resolves++; throw new InvalidOperationException("must not resolve"); };
        controller.ConnectGroupRaopOutput = (_, _, _) => { connects++; throw new InvalidOperationException("must not connect"); };
        controller.StartGroup(Plan(device) with { LocalOutputs = [new("CAPTURE", "Feedback")] }, 120, 10);
        await Until(() => controller.State == StreamState.Retrying);
        Assert.Contains(L.T(LocalOutputSink.CapturedOutputText), controller.StatusText);
        Assert.Equal(0, resolves);
        Assert.Equal(0, connects);
        Assert.Null(controller.Capture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cinema_with_RAOP_or_local_is_refused_before_resolution_and_disposed_exactly_once(bool local)
    {
        var device = local ? Ap2() : Raop(5678);
        var source = new Source();
        int resolves = 0, captures = 0;
        using var controller = Controller(new Capture(), device);
        controller.CaptureFactory = _ => { captures++; throw new InvalidOperationException("must not capture"); };
        controller.ResolveGroupMembers = (_, _) => { resolves++; throw new InvalidOperationException("must not resolve"); };
        var plan = local ? Plan(device) with { LocalOutputs = [new("local", "Local")] } : Plan(device);
        controller.StartGroup(plan, 120, 10, source: source);
        Assert.Equal(StreamState.Idle, controller.State);
        Assert.Contains("本地影音文件", controller.StatusText);
        Assert.Equal(1, source.Disposals);
        controller.Stop();
        Assert.Equal(1, source.Disposals);
        Assert.Equal(0, resolves);
        Assert.Equal(0, captures);
    }

    [Theory]
    [InlineData(ReceiverProtocol.Auto, false)]
    [InlineData(ReceiverProtocol.AirPlay2Realtime, false)]
    [InlineData(ReceiverProtocol.AirPlay2Buffered, false)]
    [InlineData(ReceiverProtocol.Raop, true)]
    public async Task Third_party_AP2_remains_guarded_except_explicit_RAOP_override(ReceiverProtocol protocol, bool allowed)
    {
        using var fake = new FakeRaop();
        var device = Raop(fake.Port) with
        {
            HasAirPlay = true, Model = "Third-party", Txt = new Dictionary<string, string> { ["srcvers"] = "300.0" },
        };
        var preferences = ReceiverPreferences.Of(device) with { ProtocolOverride = protocol };
        using var controller = Controller(new Capture(), device);
        controller.ReceiverSettings = _ => preferences;
        controller.StartGroup(Plan(device), 120, 10);
        if (allowed)
        {
            await Until(() => controller.State == StreamState.Streaming);
            Assert.Single(controller.RaopGroupOutputs);
        }
        else
        {
            Assert.Equal(StreamState.Idle, controller.State);
            Assert.Contains("第三方", controller.StatusText);
            Assert.Empty(fake.Methods);
        }
    }

    [Fact]
    public async Task Optional_busy_output_waits_independently_and_never_claims_the_live_AP2_session_as_its_own()
    {
        using var fake = new FakeRaop();
        using var receiver = new FakeReceiver();
        var raop = Raop(fake.Port);
        var ap2 = Ap2();
        var capture = new Capture();
        var busy = new BusyReader { RaopBusy = true };
        using var retry = new SemaphoreSlim(0);
        using var controller = Controller(capture, raop, ap2);
        controller.BusyReader = busy;
        int prepares = 0, connects = 0, retries = 0;
        FakeMember? homepod = null;
        controller.PrepareGroupMember = (m, _, channels, _) =>
        {
            Interlocked.Increment(ref prepares);
            homepod = new FakeMember(m.Label, receiver, new(), channels);
            return Task.FromResult<IGroupMember>(homepod);
        };
        controller.ConnectGroupRaopOutput = (o, options, ct) =>
        {
            Interlocked.Increment(ref connects);
            return RaopClient.ConnectAsync(o.Address, o.Port, options, ct);
        };
        controller.RetryGroupRaopOutput = (_, ct) => { Interlocked.Increment(ref retries); return retry.WaitAsync(ct); };
        controller.StartGroup(Plan(raop, ap2), 120, 10, trigger: ConnectTrigger.Automatic);
        await Until(() => controller.State == StreamState.Streaming && Volatile.Read(ref retries) == 1);
        Assert.True(controller.OwnSession.Live);
        Assert.Equal(0, connects);
        Assert.Empty(fake.Methods);
        Assert.DoesNotContain(busy.Reads, targets => targets.Length > 1); // optional RAOP was excluded from whole-group wait
        retry.Release();
        await Until(() => Volatile.Read(ref retries) == 2);
        Assert.Equal(0, connects); // retry is Automatic with oursRecently=false even while this group's AP2 is live
        busy.RaopBusy = false;
        retry.Release();
        await Until(() => controller.RaopGroupOutputs[0].State == OutputState.Streaming);
        Assert.Equal(1, connects);
        Assert.Equal(1, prepares);
        Assert.Same(capture, controller.Capture);
        Assert.False(homepod!.Disposed);
        Assert.All(busy.Reads.SelectMany(r => r).Where(t => GroupPlan.Key(t.DeviceId) == "AABB"), t => Assert.Equal(fake.Port, t.Port));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initial_manual_refusal_accepts_secondary_member_and_rejects_removed_or_stale_generation(bool password)
    {
        using var fake = new FakeRaop();
        using var receiver = new FakeReceiver();
        var raop = Raop(fake.Port);
        var ap2 = Ap2();
        var busy = new BusyReader { RaopBusy = !password };
        using var controller = Controller(new Capture(), ap2, raop);
        controller.BusyReader = busy;
        if (password) controller.ConnectGroupRaopOutput = (_, _, _) => Task.FromException<RaopClient>(new RaopPasswordRequiredException());
        controller.PrepareGroupMember = (m, _, channels, _) => Task.FromResult<IGroupMember>(new FakeMember(m.Label, receiver, new(), channels));
        var requested = new TaskCompletionSource<RaopInteraction>(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        controller.RaopTakeoverRequired += request => { Interlocked.Increment(ref requests); requested.TrySetResult(request); };
        controller.RaopPasswordRequired += request => { Interlocked.Increment(ref requests); requested.TrySetResult(request); };
        controller.StartGroup(Plan(ap2, raop), 120, 10);
        var request = await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => controller.State == StreamState.Streaming);
        Assert.True(controller.MatchesRaopGroupInteraction(request));
        Assert.False(controller.MatchesRaopGroupInteraction(request with { Device = raop with { DeviceId = "removed" } }));
        Assert.False(controller.MatchesRaopGroupInteraction(request with { GroupGeneration = null }));
        Assert.True(Assert.Single(controller.RaopGroupOutputs).NeedsUserAction);
        Assert.Empty(fake.Methods);
        controller.StartGroup(Plan(ap2), 120, 10);
        Assert.False(controller.MatchesRaopGroupInteraction(request));
        controller.Stop();
        Assert.False(controller.MatchesRaopGroupInteraction(request));
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Local_only_and_RAOP_plus_local_groups_start_without_any_AP2_setup(bool includeRaop)
    {
        using var fake = new FakeRaop();
        var devices = includeRaop ? new[] { Raop(fake.Port) } : [];
        var capture = new Capture();
        var local = new FakeLocalRenderDevice();
        using var controller = Controller(capture, devices);
        controller.OpenLocalOutput = _ => local;
        controller.PrepareGroupMember = (_, _, _, _) => throw new InvalidOperationException("no AP2 setup expected");
        controller.StartGroup(Plan(devices) with { LocalOutputs = [new("local", "Local") { ExtraLatencyMs = 200 }] }, 120, 10);
        await Until(() => controller.State == StreamState.Streaming);
        Assert.Null(controller.ActiveSender);
        Assert.Equal(includeRaop ? 2020 : 242, controller.EffectiveLatencyMs);
        Assert.Single(controller.LocalGroupOutputs);
        Assert.Equal(includeRaop ? 1 : 0, controller.RaopGroupOutputs.Count);
        controller.Stop();
        Assert.True(local.Disposed);
        Assert.Equal(1, capture.Disposals);
    }

    [Fact]
    public async Task Independent_recovery_before_group_has_streamed_does_not_raise_a_second_initial_dialog()
    {
        using var receiver = new FakeReceiver();
        var raop = Raop(5678);
        var ap2 = Ap2();
        using var controller = Controller(new Capture(), ap2, raop);
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0, dialogs = 0;
        controller.ConnectGroupRaopOutput = (_, _, _) => Task.FromException<RaopClient>(
            Interlocked.Increment(ref attempts) == 1 ? new IOException("offline") : new RaopPasswordRequiredException());
        controller.RetryGroupRaopOutput = (_, _) => Task.CompletedTask;
        controller.RaopPasswordRequired += _ => Interlocked.Increment(ref dialogs);
        controller.PrepareGroupMember = async (m, _, channels, ct) =>
        {
            prepared.TrySetResult();
            await release.Task.WaitAsync(ct);
            return new FakeMember(m.Label, receiver, new(), channels);
        };
        controller.StartGroup(Plan(ap2, raop), 120, 10);
        await prepared.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => Volatile.Read(ref attempts) == 2);
        Assert.Equal(0, dialogs);
        release.TrySetResult();
        await Until(() => controller.State == StreamState.Streaming);
        Assert.True(Assert.Single(controller.RaopGroupOutputs).NeedsUserAction);
        Assert.Equal(0, dialogs);
    }

    [Fact]
    public async Task Required_busy_output_waits_before_capture_and_uses_its_advertised_port()
    {
        var raop = Raop(5678);
        var busy = new BusyReader { RaopBusy = true };
        using var controller = Controller(new Capture(), raop);
        controller.BusyReader = busy;
        controller.StartGroup(Plan(raop) with { RequiredOutputs = new Dictionary<string, bool> { ["aa-bb"] = true } },
            120, 10, trigger: ConnectTrigger.Automatic);
        await Until(() => controller.WaitingForSpeaker);
        Assert.Null(controller.Capture);
        var target = Assert.Single(busy.Reads.First());
        Assert.Equal(5678, target.Port);
        Assert.Contains("RAOP", controller.StatusText);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("unsupported")]
    [InlineData("busy")]
    [InlineData("refused")]
    public async Task Required_terminal_startup_failure_stops_idle_disposes_capture_and_forgets_ownership(string failure)
    {
        var raop = Raop(5678);
        var capture = new Capture();
        using var controller = Controller(capture, raop);
        int attempts = 0;
        controller.ConnectGroupRaopOutput = (_, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            Exception error = failure switch
            {
                "password" => new RaopPasswordRequiredException(), "unsupported" => new RaopUnsupportedException(),
                "busy" => new RaopBusyException(), _ => new RaopRefusedException("receiver refused"),
            };
            return Task.FromException<RaopClient>(error);
        };
        var plan = Plan(raop) with { RequiredOutputs = new Dictionary<string, bool> { ["AABB"] = true } };
        controller.OwnSession.Restore(new(OwnSession.KeyOf(plan), DateTime.UtcNow, false));
        controller.StartGroup(plan, 120, 10);
        await Until(() => Volatile.Read(ref attempts) == 1 && controller.State == StreamState.Idle && capture.Disposals == 1);
        Assert.Null(controller.Capture);
        Assert.Null(controller.OwnSession.Record(DateTime.UtcNow));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Required_receiver_takeover_during_AP2_prepare_stops_idle_without_grace_or_retry()
    {
        using var fake = new FakeRaop();
        using var receiver = new FakeReceiver();
        var raop = Raop(fake.Port);
        var ap2 = Ap2();
        var capture = new Capture();
        using var controller = Controller(capture, raop, ap2);
        var preparing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int prepares = 0;
        controller.PrepareGroupMember = async (m, _, channels, ct) =>
        {
            Interlocked.Increment(ref prepares);
            preparing.TrySetResult();
            await release.Task.WaitAsync(ct);
            return new FakeMember(m.Label, receiver, new(), channels);
        };
        controller.StartGroup(Plan(raop, ap2) with { RequiredOutputs = new Dictionary<string, bool> { ["AABB"] = true } }, 120, 10);
        await preparing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fake.CloseConnection(reset: false);
        await fake.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.TrySetResult();
        await Until(() => controller.State == StreamState.Idle && capture.Disposals == 1);
        Assert.Equal(1, prepares);
        Assert.Contains(RaopClient.ClosedByReceiver, controller.StatusText);
        Assert.Null(controller.Capture);
        Assert.Null(controller.OwnSession.Record(DateTime.UtcNow));
    }
}
