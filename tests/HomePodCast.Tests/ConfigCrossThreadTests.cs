using System.Net;
using System.Text.Json;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

/// <summary>
/// The UI thread changes the live config; the connection queue and the stream threads never read a list or dictionary
/// of it while it changes (enumerating one then throws, and the connect was dropped with only a log line).
/// </summary>
public class ConfigCrossThreadTests
{
    [Fact]
    public async Task Connect_hands_the_queue_a_copy_taken_on_the_calling_thread()
    {
        var config = MixerRoutingV2Tests.Speaker();
        config.LatencyMs = 120;
        var app = TestApp.Create(config);
        var queue = new LatestRequestQueue("test connection");
        MixerControllerWiringTests.Set(app, "_connection", queue);
        var connected = new TaskCompletionSource<AppConfig>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var mixer = new MixerOutputCoordinator(new(), (d, _) => { connected.TrySetResult(d.Config!); return new IdleSession(); },
            (_, _) => new MixerCoordinatorTests.FakeCapture(), () => new MixerControllerWiringTests.DormantHost());
        MixerControllerWiringTests.Set(app, "<Mixer>k__BackingField", mixer);
        using var stopping = new ManualResetEventSlim();
        _ = queue.Post(stopping.Wait); // a slow Stop still runs

        app.Connect(ConnectTrigger.Manual);
        config.LatencyMs = 999; // the UI thread goes on changing the live config meanwhile
        stopping.Set();

        Assert.Equal(120, (await connected.Task.WaitAsync(TimeSpan.FromSeconds(10))).LatencyMs);
    }

    [Fact]
    public void Selecting_a_receiver_replaces_the_settings_a_stream_thread_may_be_reading()
    {
        var config = TestApp.Config();
        var app = TestApp.Create(config);
        var read = config.ReceiverSettings; // what a controller's ReceiverSettings lookup holds right now

        app.SelectDevice(new AirPlayDevice("Test", "02:11", IPAddress.Parse("192.0.2.20"), 7000, "ThirdParty",
            new Dictionary<string, string> { ["features"] = "0x0,0x300" }));

        Assert.Empty(read);
        Assert.True(config.ReceiverSettings.ContainsKey(RaopPassword.Key("02:11")));
    }

    [Fact]
    public void Receiver_collections_are_replaced_never_changed_under_a_reader()
    {
        var cfg = new AppConfig();
        var device = new AirPlayDevice("Test", "02:11", IPAddress.Loopback, 7001, "ThirdParty", new Dictionary<string, string>());
        var settingsBefore = cfg.ReceiverSettings;
        var passwordsBefore = cfg.RaopPasswordsProtected;
        var manualBefore = cfg.ManualDevices;

        var made = cfg.ReceiverPreferencesFor("0211", () => ReceiverPreferences.Of(device));
        Assert.Same(made, cfg.ReceiverPreferencesFor("0211", () => throw new InvalidOperationException("made twice")));
        cfg.SetRaopPassword("0211", "protected");
        cfg.AddManualDevice(new ManualAirPlayDevice("Test", "02:11", "192.0.2.20", 7000, "", []));
        cfg.AddManualDevice(new ManualAirPlayDevice("Test", "02:11", "192.0.2.21", 7000, "", [])); // same id: replaced

        Assert.Empty(settingsBefore); Assert.Empty(passwordsBefore); Assert.Empty(manualBefore);
        Assert.Same(made, cfg.ReceiverSettings["0211"]);
        Assert.Equal("protected", cfg.RaopPasswordsProtected["0211"]);
        Assert.Equal("192.0.2.21", Assert.Single(cfg.ManualDevices).Host);
        var withEntry = cfg.ReceiverSettings;
        cfg.SetReceiverPreferences("0211", null);
        Assert.Empty(cfg.ReceiverSettings); Assert.Single(withEntry);
    }

    [Fact]
    public void A_snapshot_shares_no_collection_with_the_live_config()
    {
        var cfg = new AppConfig { DeviceId = "AA:BB:CC:DD:EE:FF", Hotkeys = { [HotkeyAction.VolumeUp] = "Ctrl+Alt+PageUp" } };
        cfg.SetReceiverPreferences("0211", ReceiverPreferences.Of(
            new AirPlayDevice("Test", "02:11", IPAddress.Loopback, 7001, "ThirdParty", new Dictionary<string, string>())));
        var copy = cfg.Snapshot();
        Assert.NotSame(cfg.ReceiverSettings, copy.ReceiverSettings);
        Assert.NotSame(cfg.Hotkeys, copy.Hotkeys);
        Assert.Equal(JsonSerializer.Serialize(cfg), JsonSerializer.Serialize(copy));
    }

    private sealed class IdleSession : IMixerDestinationSession
    {
        public StreamController Controller { get; } = new();
        public StreamState State => StreamState.Idle;
        public string Status => "fake";
        public int EffectiveLatencyMs => 0;
        public event Action? Changed { add { } remove { } }
        public void Start(ConnectTrigger trigger) { }
        public Task StopAsync(string? reason = null) => Task.CompletedTask;
        public void Dispose() => Controller.Dispose();
    }
}
