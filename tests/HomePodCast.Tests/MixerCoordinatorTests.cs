using System.Net;
using System.Text.Json;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

public sealed class MixerCoordinatorTests
{
    [Fact]
    public void Lease_acquisition_is_atomic_and_normalizes_protocol_aliases()
    {
        var leases = new MixerDeviceLeaseTable();
        using var a = leases.Acquire("a", ["airplay:aa-bb-cc-dd-ee-ff"]);
        Assert.Throws<MixerLeaseException>(() => leases.Acquire("b", ["airplay:112233445566", "raop:AA:BB:CC:DD:EE:FF"]));
        Assert.Equal(1, leases.Count);
        using var c = leases.Acquire("c", ["airplay:112233445566"]);
        Assert.Equal(2, leases.Count);
    }

    [Fact]
    public void Cross_protocol_aliases_require_confirmed_physical_identities()
    {
        var leases = new MixerDeviceLeaseTable();
        var identities = new Dictionary<string, string>
        { ["airplay:AABB"] = "one", ["dlna:uuid:render"] = "one", ["local:endpoint"] = "two" };
        using var a = leases.Acquire("a", ["airplay:AABB"], identities);
        var conflict = Assert.Throws<MixerLeaseException>(() => leases.Acquire("b", ["dlna:UUID:RENDER"], identities));
        Assert.Equal(MixerLeaseError.Occupied, conflict.Conflict.Error);
        using var c = leases.Acquire("c", ["local:endpoint"], identities);
        Assert.Throws<MixerLeaseException>(() => leases.Acquire("unknown", ["dlna:uuid:other"]));
    }

    [Fact]
    public void Refreshing_pair_members_cannot_steal_a_live_speaker()
    {
        var leases = new MixerDeviceLeaseTable();
        using var pair = leases.Acquire("pair", ["pair:room", "airplay:AABB", "airplay:CCDD"]);
        using var single = leases.Acquire("single", ["airplay:EEFF"]);
        Assert.Throws<MixerLeaseException>(() => leases.Refresh("pair", ["pair:room", "airplay:AABB", "airplay:EEFF"], new Dictionary<string, string>()));
        Assert.Throws<MixerLeaseException>(() => leases.Acquire("still held", ["airplay:CCDD"]));
        pair.Dispose();
        using var released = leases.Acquire("released", ["airplay:CCDD"]);
    }

    [Fact]
    public async Task Concurrent_starts_allow_one_owner_and_one_session()
    {
        var leases = new MixerDeviceLeaseTable();
        var first = new Harness(leases); var second = new Harness(leases);
        await using var a = first.Coordinator; await using var b = second.Coordinator;
        var results = await Task.WhenAll(a.ConnectAsync(Config(), ConnectTrigger.Manual), b.ConnectAsync(Config(), ConnectTrigger.Manual));
        Assert.Single(results, r => r.Result == MixerEditResult.Applied);
        Assert.Single(results, r => r.Result == MixerEditResult.Conflict);
        Assert.Equal(1, first.Sessions.Count + second.Sessions.Count);
    }

    [Fact]
    public async Task Saved_rules_do_not_connect_but_running_roots_rent_and_release_destinations()
    {
        var harness = new Harness(); await using var coordinator = harness.Coordinator;
        var config = Config(); var routing = Routes(config);
        Assert.Empty(harness.Sessions);
        Assert.Equal(MixerEditResult.Applied, (await coordinator.ConnectAsync(config, ConnectTrigger.Manual)).Result);
        Assert.Single(harness.Sessions);
        Assert.Equal(1, harness.Hosts);
        await coordinator.RefreshRootsAsync([new(2, "music", false)]);
        Assert.Equal(2, coordinator.Statuses.Count);
        var game = harness.Sessions["speaker:airplay:AABBCCDDEEFF"];
        var music = harness.Sessions["group:music"];
        Assert.Equal(105, game.Config.LatencyMs); Assert.Equal(2000, music.Config.LatencyMs);
        music.Fail(StreamState.Retrying, "slow prepare");
        Assert.Equal(StreamState.Streaming, game.State);
        Assert.Equal(1, game.Starts);
        await coordinator.RefreshRootsAsync([]);
        Assert.Single(coordinator.Statuses); Assert.Equal(1, music.Stops); Assert.Equal(0, game.Stops);
        Assert.Equal(1, coordinator.Leases.Count);
    }

    [Fact]
    public async Task Explicit_takeover_authorization_is_scoped_to_one_destination()
    {
        var harness = new Harness(); await using var coordinator = harness.Coordinator;
        var config = Config(); Routes(config);
        await coordinator.RefreshRootsAsync([new(2, "music", false)]);
        await coordinator.ConnectAsync(config, ConnectTrigger.TakeOver, "group:music");
        Assert.Equal(ConnectTrigger.TakeOver, harness.Sessions["group:music"].LastTrigger);
        Assert.Equal(ConnectTrigger.Manual, harness.Sessions["speaker:airplay:AABBCCDDEEFF"].LastTrigger);
        await coordinator.ReconnectAsync("group:music", ConnectTrigger.Manual);
        Assert.Equal(1, harness.Sessions["speaker:airplay:AABBCCDDEEFF"].Starts);
    }

    [Fact]
    public async Task Native_Local_never_starts_capture_or_connections()
    {
        var harness = new Harness(); await using var coordinator = harness.Coordinator;
        var config = Config(); MixerRoutes.Write(config, new() { Default = MixerRoute.Local });
        await coordinator.ConnectAsync(config, ConnectTrigger.Manual);
        Assert.Empty(harness.Sessions); Assert.Equal(0, harness.Hosts); Assert.Equal(0, harness.LegacyCaptures);
    }

    [Fact]
    public async Task Same_destination_reuses_one_controller_and_does_not_make_a_hub()
    {
        var harness = new Harness(); await using var coordinator = harness.Coordinator;
        var config = Config(); MixerRoutes.Write(config, new() { Apps = new()
            { ["game"] = MixerRoutingV2Tests.Named("airplay:aa-bb-cc-dd-ee-ff"), ["chat"] = MixerRoute.Current with { AlsoLocal = true } } });
        await coordinator.ConnectAsync(config, ConnectTrigger.Manual);
        await coordinator.RefreshRootsAsync([new(1, "game", false), new(2, "chat", false)]);
        Assert.Single(harness.Sessions); Assert.Equal(0, harness.Hosts);
        Assert.Equal(1, harness.LegacyCaptures);
        Assert.Equal(0, harness.Sessions.Values.Single().Capture!.ExtraLatencyMs);
    }

    [Fact]
    public async Task Leases_are_retained_until_teardown_really_completes()
    {
        var harness = new Harness(); await using var coordinator = harness.Coordinator;
        await coordinator.ConnectAsync(Config(), ConnectTrigger.Manual);
        var session = harness.Sessions.Values.Single();
        session.StopGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopping = coordinator.DisconnectAsync();
        Assert.Equal(1, coordinator.Leases.Count);
        Assert.Throws<MixerLeaseException>(() => coordinator.Leases.Acquire("cinema", ["airplay:AABBCCDDEEFF"]));
        session.StopGate.SetResult(); await stopping;
        Assert.Equal(0, coordinator.Leases.Count);
        using var cinema = coordinator.Leases.Acquire("cinema", ["airplay:AABBCCDDEEFF"]);
    }

    [Fact]
    public async Task Switch_all_counts_inactive_rules_is_one_save_and_rolls_back_on_failure()
    {
        var harness = new Harness(); await using var coordinator = harness.Coordinator;
        var config = Config(); Routes(config); config.AppSendVolumes["music"] = 24;
        var preview = coordinator.PreviewSwitch(config); Assert.Equal(1, preview.Overrides);
        string before = JsonSerializer.Serialize(config); int saves = 0;
        var failed = await coordinator.SwitchAllAsync(config, MixerRoute.Local, preview, () => { saves++; throw new IOException(); });
        Assert.Equal(MixerEditResult.SaveFailed, failed.Result); Assert.Equal(before, JsonSerializer.Serialize(config));
        Assert.Equal(1, saves);
        var result = await coordinator.SwitchAllAsync(config, MixerRoute.Local, preview, () => saves++);
        Assert.Equal(MixerEditResult.Applied, result.Result); Assert.Equal(2, saves);
        Assert.Empty(MixerRoutes.Read(config).Settings!.Apps);
        Assert.Equal(24, config.AppSendVolumes["music"]);
        Assert.Null(config.CaptureDeviceId);
    }

    [Fact]
    public async Task Confirmation_is_recomputed_after_a_concurrent_edit_and_conflicts_change_nothing()
    {
        var harness = new Harness(); await using var coordinator = harness.Coordinator;
        var config = Config(); var settings = Routes(config); var preview = coordinator.PreviewSwitch(config);
        settings.Apps["inactive"] = MixerRoute.Local; MixerRoutes.Write(config, settings);
        Assert.Equal(MixerEditResult.Stale, (await coordinator.SwitchAllAsync(config, MixerRoute.Local, preview, () => throw new Exception())).Result);
        using var player = coordinator.Leases.Acquire("player", ["airplay:AABBCCDDEEFF"]);
        string before = JsonSerializer.Serialize(config);
        Assert.Equal(MixerEditResult.Conflict, (await coordinator.SwitchAllAsync(config, MixerRoute.Current, coordinator.PreviewSwitch(config), () => throw new Exception())).Result);
        Assert.Equal(before, JsonSerializer.Serialize(config));
    }

    [Fact]
    public async Task Switch_all_stops_overlapping_old_destinations_before_starting_the_new_one()
    {
        var harness = new Harness(); await using var coordinator = harness.Coordinator;
        var config = Config(); Routes(config);
        await coordinator.ConnectAsync(config, ConnectTrigger.Manual);
        await coordinator.RefreshRootsAsync([new(2, "music", false)]);
        var group = new MixerRoute(new MixerOutput(MixerOutputKind.Group, "music"));
        Assert.Equal(MixerEditResult.Applied, (await coordinator.SwitchAllAsync(config, group, coordinator.PreviewSwitch(config), () => { })).Result);
        Assert.Single(coordinator.Statuses);
        Assert.Equal("group:music", coordinator.Statuses.Single().Key);
        Assert.Equal(1, coordinator.Leases.Count);
    }

    [Fact]
    public async Task Single_disconnect_and_takeover_do_not_restart_or_stop_the_other_destination()
    {
        var harness = new Harness(); await using var coordinator = harness.Coordinator;
        var config = Config(); Routes(config);
        await coordinator.ConnectAsync(config, ConnectTrigger.Manual);
        await coordinator.RefreshRootsAsync([new(2, "music", false)]);
        var game = harness.Sessions["speaker:airplay:AABBCCDDEEFF"];
        harness.Sessions["group:music"].Fail(StreamState.Idle, "taken over");
        await coordinator.RefreshRootsAsync([new(2, "music", false)]);
        Assert.Equal(1, harness.Sessions["group:music"].Starts);
        await coordinator.DisconnectAsync("group:music");
        Assert.Equal(0, game.Stops); Assert.Equal(1, game.Starts);
        await coordinator.RefreshRootsAsync([new(2, "music", false)]);
        Assert.Single(coordinator.Statuses);
        await coordinator.DisconnectAsync(); Assert.Equal(0, coordinator.Leases.Count);
    }

    [Fact]
    public async Task Unknown_formats_are_preserved_and_ambiguous_app_delay_has_no_fallback()
    {
        var harness = new Harness(); await using var coordinator = harness.Coordinator;
        var config = Config(); Routes(config);
        await coordinator.ConnectAsync(config, ConnectTrigger.Manual);
        await coordinator.RefreshRootsAsync([new(2, "music", false)]);
        Assert.Null(coordinator.DestinationForApp(null));
        Assert.Equal("group:music", coordinator.DestinationForApp("Music.EXE")!.Key);
        config.MixerRouting = JsonDocument.Parse("{\"Version\":3,\"new\":true}").RootElement.Clone();
        string before = config.MixerRouting.Value.GetRawText();
        Assert.Equal(MixerEditResult.InvalidConfig, (await coordinator.EditAsync(config, new(), () => throw new Exception())).Result);
        Assert.Equal(before, config.MixerRouting.Value.GetRawText());
    }

    internal static AppConfig Config()
    {
        var c = MixerRoutingV2Tests.Speaker(); c.Scene = Scene.Game; c.LatencyMs = 105;
        c.GroupSpeakers!.Add(new() { DeviceId = "112233445566", Name = "music" });
        return c;
    }
    internal static MixerRoutingSettings Routes(AppConfig c)
    {
        OutputGroups.Save(c, "music", new() { DeviceId = "112233445566", DeviceName = "music", GroupDelayMs = 2000 }, "music");
        var s = new MixerRoutingSettings { Apps = new() { ["music"] = new(new MixerOutput(MixerOutputKind.Group, "music")) },
            DestinationSettings = new() { ["group:music"] = new(Scene.Music, 2000) } };
        MixerRoutes.Write(c, s); return s;
    }

    internal sealed class Harness
    {
        public readonly Dictionary<string, FakeSession> Sessions = [];
        public readonly MixerOutputCoordinator Coordinator;
        public int Hosts, LegacyCaptures;
        public Harness(MixerDeviceLeaseTable? leases = null)
        {
            Coordinator = new(leases ?? new(), (d, capture) => Sessions[d.Key] = new(d.Config!, capture),
                (_, _) => { LegacyCaptures++; return new FakeCapture(); }, () => { Hosts++; return new FakeHost(); });
        }
    }
    internal sealed class FakeSession(AppConfig config, Func<AudioFifo, ICaptureSource> capture) : IMixerDestinationSession
    {
        public AppConfig Config => config;
        public ICaptureSource? Capture;
        public int Starts, Stops;
        public ConnectTrigger LastTrigger;
        public TaskCompletionSource? StopGate;
        public StreamController? Controller => null;
        public StreamState State { get; private set; }
        public string Status { get; private set; } = "";
        public int EffectiveLatencyMs => config.LatencyMs;
        public event Action? Changed;
        public void Start(ConnectTrigger trigger) { Starts++; LastTrigger = trigger; Capture ??= capture(new(44100, 16, 58)); Capture.Start(); State = StreamState.Streaming; Changed?.Invoke(); }
        public async Task StopAsync(string? reason = null)
        { Stops++; if (StopGate != null) await StopGate.Task; Capture?.Dispose(); Capture = null; State = StreamState.Idle; }
        public void Fail(StreamState state, string status) { State = state; Status = status; Changed?.Invoke(); }
        public void Dispose() { }
    }
    private sealed class FakeHost : IMixerCaptureHost
    {
        private readonly MixerCaptureHub _hub = new(44100, _ => new MixerCaptureBusTests.FakeInput(.1f));
        public IReadOnlyList<RouteTarget> Roots => [];
        public event Action? RootsChanged { add { } remove { } }
        public void Start() { }
        public void Update(AppConfig config, MixerRoutingSettings routing) { }
        public ICaptureSource Subscribe(string key, AudioFifo fifo) => _hub.Subscribe(key, fifo);
        public void Dispose() => _hub.Dispose();
    }
    internal sealed class FakeCapture : ICaptureSource
    {
        public string? DeviceName => "fake endpoint";
        public double DriftPpm => 0;
        public float Peak => 0;
        public int ExtraLatencyMs => 0;
        public event Action<string>? DeviceChanged { add { } remove { } }
        public void Start() { }
        public void Dispose() { }
    }
}
