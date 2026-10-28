using HomePodCast.Audio;
using HomePodCast.Net;
using HomePodCast.UI;
using System.Reflection;
using System.Net;

namespace HomePodCast.Tests;

public sealed class MixerBoundaryTests
{
    [Fact]
    public async Task A_named_receiver_busy_prompt_can_only_resume_its_own_current_session_after_confirmation()
    {
        var config = MixerCoordinatorTests.Config(); MixerCoordinatorTests.Routes(config);
        var app = TestApp.Create(config); using var initial = app.Controller;
        var sessions = new Dictionary<string, GuardSession>();
        await using var mixer = new MixerOutputCoordinator(new(), (d, _) => sessions[d.Key] = new(),
            (_, _) => new MixerCoordinatorTests.FakeCapture(), () => new DormantHost());
        typeof(TrayApp).GetField("<Mixer>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, mixer);
        await mixer.RefreshRootsAsync([new(1, "music", false)]); await mixer.ConnectAsync(config, ConnectTrigger.Manual);
        var music = sessions["group:music"];
        var receiver = new AirPlayDevice("fake music receiver", "112233445566", IPAddress.Loopback, 7000, "", new Dictionary<string, string>());
        await app.HandleMixerTakeoverAsync("group:music", music.Controller, new(receiver, ConnectTrigger.Automatic), _ => throw new Exception("automatic prompt"));
        await app.HandleMixerTakeoverAsync("group:music", music.Controller, new(receiver, ConnectTrigger.Manual), _ => false);
        Assert.Equal(1, music.Starts);
        await app.HandleMixerTakeoverAsync("group:music", initial, new(receiver, ConnectTrigger.Manual), _ => throw new Exception("stale controller prompt"));
        await app.HandleMixerTakeoverAsync("group:music", music.Controller, new(receiver, ConnectTrigger.Manual), _ => true);
        Assert.Equal(2, music.Starts); Assert.Equal(ConnectTrigger.TakeOver, music.LastTrigger);
        Assert.Equal(1, sessions["speaker:airplay:AABBCCDDEEFF"].Starts);
    }

    [Fact]
    public void Volume_keys_end_a_global_mute_but_alignment_preserves_it() => Sta.Run(() =>
    {
        var config = MixerCoordinatorTests.Config(); MixerCoordinatorTests.Routes(config);
        var app = TestApp.Create(config); using var original = app.Controller;
        var coordinator = new MixerOutputCoordinator(new(), (_, _) => new GuardSession(),
            (_, _) => new MixerCoordinatorTests.FakeCapture(), () => new DormantHost());
        try
        {
            coordinator.RefreshRootsAsync([new(2, "music", false)]).GetAwaiter().GetResult();
            coordinator.ConnectAsync(config, ConnectTrigger.Manual).GetAwaiter().GetResult();
            Set("<Mixer>k__BackingField", coordinator); Set("<Controller>k__BackingField", coordinator.SelectedController!);
            using var form = new MainWindow(app); Set("_form", form);
            app.SetSpeakerMuted(true);
            var setVolume = typeof(TrayApp).GetMethod("SetVolumeLive", BindingFlags.Instance | BindingFlags.NonPublic)!;
            setVolume.Invoke(app, [12d, true, false]);
            Assert.All(coordinator.Controllers, c => Assert.True(c.Muted));
            setVolume.Invoke(app, [13d, false, true]);
            Assert.All(coordinator.Controllers, c => Assert.False(c.Muted));
            Assert.False(TestApp.Field<bool>(app, "_mixerMuted"));
        }
        finally { coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        void Set(string name, object value) => typeof(TrayApp).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, value);
    });

    [Fact]
    public async Task Changing_Current_cannot_reuse_a_controller_still_owned_by_the_old_named_destination()
    {
        var config = MixerRoutingV2Tests.Speaker();
        var app = TestApp.Create(config);
        var original = app.Controller;
        var create = typeof(TrayApp).GetMethod("CreateMixerSession", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await using var coordinator = new MixerOutputCoordinator(new(), (d, capture) =>
            new DormantSession((IMixerDestinationSession)create.Invoke(app, [d, capture])!),
            (_, _) => new MixerCoordinatorTests.FakeCapture(), () => new DormantHost());
        typeof(TrayApp).GetField("<Mixer>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, coordinator);
        await coordinator.ConnectAsync(config, ConnectTrigger.Manual);
        Assert.Same(original, coordinator.SelectedController);
        config.DeviceId = "112233445566"; config.DeviceName = "new Current";
        config.GroupSpeakers!.Add(new() { DeviceId = config.DeviceId, Name = config.DeviceName });
        MixerRoutes.Write(config, new() { Apps = new() { ["old"] = MixerRoutingV2Tests.Named("airplay:AABBCCDDEEFF") } });
        await coordinator.RefreshRootsAsync([new(1, "old", false)]);
        await coordinator.ConnectAsync(config, ConnectTrigger.Automatic);
        Assert.Equal(2, coordinator.Controllers.Count);
        Assert.Same(original, coordinator.ControllerForKey("speaker:airplay:AABBCCDDEEFF"));
        Assert.NotSame(original, coordinator.ControllerForKey("speaker:airplay:112233445566"));
        var created = coordinator.ControllerForKey("speaker:airplay:112233445566")!;
        Assert.NotNull(typeof(StreamController).GetField("RaopTakeoverRequired", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(created));
    }

    [Fact]
    public async Task A_destination_created_in_night_mode_starts_with_reduce_bass()
    {
        var config = MixerRoutingV2Tests.Speaker();
        config.Effects.OutputEq = new EqSettings { Preset = "bass-boost" };
        var app = TestApp.Create(config);
        var create = typeof(TrayApp).GetMethod("CreateMixerSession", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await using var coordinator = new MixerOutputCoordinator(new(), (d, capture) =>
            new DormantSession((IMixerDestinationSession)create.Invoke(app, [d, capture])!),
            (_, _) => new MixerCoordinatorTests.FakeCapture(), () => new DormantHost());
        typeof(TrayApp).GetField("<Mixer>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, coordinator);
        await coordinator.ConnectAsync(config, ConnectTrigger.Manual);
        config.NightMode = true;
        config.DeviceId = "112233445566"; config.DeviceName = "new Current";
        config.GroupSpeakers!.Add(new() { DeviceId = config.DeviceId, Name = config.DeviceName });
        MixerRoutes.Write(config, new() { Apps = new() { ["old"] = MixerRoutingV2Tests.Named("airplay:AABBCCDDEEFF") } });
        await coordinator.RefreshRootsAsync([new(1, "old", false)]);
        await coordinator.ConnectAsync(config, ConnectTrigger.Automatic);
        var created = coordinator.ControllerForKey("speaker:airplay:112233445566")!;
        var equalizers = TestApp.Field<System.Collections.Concurrent.ConcurrentDictionary<StreamController, Equalizer>>(app, "_mixerEqualizers");
        Assert.Equal(EqPreset.ReduceBass, equalizers[created].Preset);
    }

    [Fact]
    public void Named_single_destination_bypass_uses_the_live_routing_policy()
    {
        var config = MixerRoutingV2Tests.Speaker(); var app = TestApp.Create(config);
        using var controller = app.Controller;
        var method = typeof(TrayApp).GetMethod("LegacyMixerCapture", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var destination = MixerDestinationResolver.Resolve(config, MixerRoutingV2Tests.Named("airplay:AABBCCDDEEFF"));
        using var capture = (RoutedCapture)method.Invoke(app, [destination, new AudioFifo(44100, 16, 58)])!;
        Assert.Same(app.Routing, TestApp.Field<AppRouting>(capture, "_routing"));
    }

    [Fact]
    public async Task Native_copy_rejects_only_the_same_physical_group_endpoint_and_updates_live()
    {
        string native = "headphones";
        var config = MixerRoutingV2Tests.Speaker();
        config.GroupLocalOutputs = [new("render", "fake render")];
        config.GroupCaptureLocalDeviceIds = ["render"];
        config.MixerDeviceIdentities = new() { ["airplay:AABBCCDDEEFF"] = "speaker", ["local:render"] = "render", ["local:headphones"] = "headphones" };
        var route = MixerRoute.Current with { AlsoLocal = true };
        MixerRoutes.Write(config, new() { Default = route });
        GuardSession? session = null;
        await using var coordinator = new MixerOutputCoordinator(new(), (d, _) => session = new(),
            (_, _) => throw new InvalidOperationException(), () => throw new InvalidOperationException(), nativeOutput: () => native);
        Assert.Equal(MixerEditResult.Applied, (await coordinator.ConnectAsync(config, ConnectTrigger.Manual)).Result);
        Assert.False(session!.Controller.MixerLocalDuplicate!("render"));
        native = "render"; coordinator.RefreshNativeDuplicateGuards();
        Assert.True(session.Controller.MixerLocalDuplicate("render"));
        Assert.Equal(MixerEditResult.InvalidDestination, coordinator.PreviewRoute(config, null, route).Result);
        native = "headphones"; coordinator.RefreshNativeDuplicateGuards();
        Assert.False(session.Controller.MixerLocalDuplicate("render"));
        config.MixerDeviceIdentities["local:headphones"] = "render";
        Assert.Equal(MixerRoutingError.DuplicateLocal, coordinator.PreviewRoute(config, null, route).Error);
        Assert.Equal("headphones", native); Assert.Null(config.CaptureDeviceId);
    }

    [Fact]
    public void Explicit_destination_delay_applies_to_an_auxiliary_only_group_without_mutating_the_preset()
    {
        var config = MixerRoutingV2Tests.Speaker();
        var preset = new OutputGroupSettings { DeviceId = null, GroupCaptureDeviceIds = [],
            GroupLocalOutputs = [new("render", "render")], GroupCaptureLocalDeviceIds = ["render"], GroupDelayMs = 500 };
        OutputGroups.Save(config, "local", preset, "local");
        var route = new MixerRoute(new(MixerOutputKind.Group, "local"));
        MixerRoutes.Write(config, new() { Default = route, DestinationSettings = new() { ["group:local"] = new(Scene.Music, 2500) } });
        var resolved = MixerDestinationResolver.Resolve(config, route);
        Assert.Equal(2500, GroupPlan.FromConfig(resolved.Config!)!.GroupDelayMs);
        Assert.Equal(500, config.SavedOutputGroups.Single().Settings.GroupDelayMs);
        Assert.Equal(500, preset.GroupDelayMs);
    }

    [Fact]
    public void Omitting_the_alias_map_cannot_bypass_an_existing_physical_lease()
    {
        var leases = new MixerDeviceLeaseTable();
        using var owner = leases.Acquire("one", ["airplay:AABB"], new Dictionary<string, string>
            { ["airplay:AABB"] = "same", ["dlna:uuid:one"] = "same" });
        Assert.Throws<MixerLeaseException>(() => leases.Acquire("two", ["dlna:uuid:one"]));
    }

    private sealed class GuardSession : IMixerDestinationSession
    {
        public int Starts;
        public ConnectTrigger LastTrigger;
        public StreamController Controller { get; } = new();
        public StreamState State => StreamState.Idle;
        public string Status => "fake";
        public int EffectiveLatencyMs => 0;
        public event Action? Changed { add { } remove { } }
        public void Start(ConnectTrigger trigger) { Starts++; LastTrigger = trigger; }
        public Task StopAsync(string? reason = null) => Task.CompletedTask;
        public void Dispose() => Controller.Dispose();
    }

    private sealed class DormantSession(IMixerDestinationSession inner) : IMixerDestinationSession
    {
        public StreamController? Controller => inner.Controller;
        public StreamState State => StreamState.Idle;
        public string Status => "fake";
        public int EffectiveLatencyMs => 0;
        public event Action? Changed { add { } remove { } }
        public void Start(ConnectTrigger trigger) { }
        public Task StopAsync(string? reason = null) => Task.CompletedTask;
        public void Dispose() => inner.Dispose();
    }
    private sealed class DormantHost : IMixerCaptureHost
    {
        public IReadOnlyList<RouteTarget> Roots => [];
        public event Action? RootsChanged { add { } remove { } }
        public void Start() { }
        public void Update(AppConfig config, MixerRoutingSettings routing) { }
        public ICaptureSource Subscribe(string key, AudioFifo fifo) => new MixerCoordinatorTests.FakeCapture();
        public void Dispose() { }
    }
}
