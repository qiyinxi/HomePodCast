using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using HomePodCast.Audio;
using HomePodCast.Net;
using HomePodCast.Players;
using HomePodCast.UI;

namespace HomePodCast.Tests;

/// <summary>
/// A/V calibration as the app keys and uses it (device checklist 「校准」): the path it belongs to is read from the capture
/// that is open (TrayApp.CalibrationContextFor), the one correction reaches 首页's estimate, the browser extension's API
/// and mpv/VLC, a test whose path changed is not saved, and 恢复默认补偿 clears only the path in use. Fake captures and
/// players; the API runs on loopback.
/// </summary>
public class CalibrationPathTests
{
    private const string Speaker = "AA:BB:CC:DD:EE:01";
    private const string Speakers = "{0.0.0.00000000}.{speakers}", Headphones = "{0.0.0.00000000}.{headphones}";

    /// <summary>What the stream captures: an endpoint, maybe through per-app routing (which adds latency).</summary>
    private sealed class FakeCapture(string? endpoint, int extraMs = 0, bool missing = false, bool noOutput = false) : ICaptureSource
    {
        public string? DeviceName => "fake";
        public double DriftPpm => 0;
        public float Peak => 0;
        public int ExtraLatencyMs => extraMs;
        public bool CaptureDeviceMissing => missing;
        public bool NoOutputDevice => noOutput;
        public string? EndpointId => endpoint;
        public event Action<string>? DeviceChanged { add { } remove { } }
        public void Start() { }
        public void Dispose() { }
    }

    private static AppConfig Config(string? captureDevice = null) => new()
    {
        DeviceId = Speaker, DeviceName = "卧室", CaptureDeviceId = captureDevice, Scene = Scene.Movie, LatencyMs = Scenes.MovieMs,
        VideoDelayExtraMs = 36,
    };

    private static AvCalibrationContext Context(AppConfig cfg, ICaptureSource capture) =>
        TrayApp.CalibrationContextFor(cfg, capture, cfg.CaptureDeviceId) ?? throw new InvalidOperationException("no path");

    [Fact]
    public void The_path_is_the_speaker_the_open_endpoint_the_default_or_chosen_device_and_the_routing_mode()
    {
        var followDefault = Context(Config(), new FakeCapture(Speakers));
        Assert.Equal(new AvCalibrationContext(GroupPlan.Key(Speaker), Speakers, PerAppRouting: false, FollowsDefault: true), followDefault);

        // The same endpoint chosen by hand is another path (choosing it may differ from what the default output did).
        var chosen = Context(Config(Speakers), new FakeCapture(Speakers));
        Assert.False(chosen.FollowsDefault);
        Assert.NotEqual(followDefault.Key, chosen.Key);

        // Per-app routing: the capture path adds latency, so its own path.
        var routed = Context(Config(), new FakeCapture(Speakers, extraMs: RoutedCapture.RoutedExtraLatencyMs));
        Assert.True(routed.PerAppRouting);
        Assert.NotEqual(followDefault.Key, routed.Key);

        // Another output device behind the default, another speaker, a multi-room group: each its own.
        Assert.NotEqual(followDefault.Key, Context(Config(), new FakeCapture(Headphones)).Key);
        var other = Config();
        other.DeviceId = "AA:BB:CC:DD:EE:02";
        Assert.NotEqual(followDefault.Key, Context(other, new FakeCapture(Speakers)).Key);
        var group = Config();
        group.MultiRoomDeviceId = "AA:BB:CC:DD:EE:02";
        Assert.NotEqual(followDefault.Key, Context(group, new FakeCapture(Speakers)).Key);
    }

    [Fact]
    public void No_usable_capture_means_no_path_so_no_calibration_is_ever_borrowed()
    {
        var cfg = Config(Headphones);
        Assert.Null(TrayApp.CalibrationContextFor(cfg, null, cfg.CaptureDeviceId));                                // not capturing
        Assert.Null(TrayApp.CalibrationContextFor(cfg, new FakeCapture(Speakers), cfg.CaptureDeviceId));          // not switched yet
        Assert.Null(TrayApp.CalibrationContextFor(cfg, new FakeCapture(null, missing: true), cfg.CaptureDeviceId)); // unplugged
        Assert.Null(TrayApp.CalibrationContextFor(Config(), new FakeCapture(null, noOutput: true), null));         // no output at all
        Assert.Null(TrayApp.CalibrationContextFor(new AppConfig(), new FakeCapture(Speakers), null));             // no speaker
        Assert.NotNull(TrayApp.CalibrationContextFor(cfg, new FakeCapture(Headphones.ToUpperInvariant()), cfg.CaptureDeviceId));

        // Not streaming: nothing to measure on.
        var context = Context(Config(), new FakeCapture(Speakers));
        Assert.Null(TrayApp.SnapshotFor(StreamState.Connecting, context, 200, 0, 20));
        Assert.Null(TrayApp.SnapshotFor(StreamState.Streaming, null, 200, 0, 20));
        Assert.Equal(new AvCalibrationSnapshot(context, 200, 0, 20), TrayApp.SnapshotFor(StreamState.Streaming, context, 200, 0, 20));
    }

    [Fact]
    public void A_calibration_is_never_reused_after_the_speaker_capture_device_default_choice_or_routing_changed()
    {
        var cfg = Config();
        var measured = Context(cfg, new FakeCapture(Speakers));
        var taken = TrayApp.SnapshotFor(StreamState.Streaming, measured, Scenes.MovieMs, 0, 20)!.Value;
        Assert.Equal(SyncCalibrationOutcome.Applied, TrayApp.StoreSyncCalibration(cfg, taken, taken, 260, out _));
        int calibrated = TrayApp.VideoDelayFor(cfg, StreamState.Streaming, Scenes.MovieMs, 0, 20, measured);
        Assert.Equal(260, calibrated);

        int Lag(AppConfig c, ICaptureSource capture) => TrayApp.VideoDelayFor(c, StreamState.Streaming, Scenes.MovieMs,
            capture.ExtraLatencyMs, 20, TrayApp.CalibrationContextFor(c, capture, c.CaptureDeviceId));
        int estimate = LatencyTuner.EstimatedSoundLagMs(Scenes.MovieMs, cfg, 0, 20);
        Assert.Equal(estimate, Lag(cfg, new FakeCapture(Headphones)));                        // the default moved to headphones
        Assert.Equal(estimate + RoutedCapture.RoutedExtraLatencyMs,
            Lag(cfg, new FakeCapture(Speakers, RoutedCapture.RoutedExtraLatencyMs)));          // per-app routing turned on
        var chosen = Config(Speakers);
        chosen.SyncCalibrations = cfg.SyncCalibrations;
        Assert.Equal(estimate, Lag(chosen, new FakeCapture(Speakers)));                       // the same device, chosen by hand
        var otherSpeaker = Config();
        otherSpeaker.DeviceId = "AA:BB:CC:DD:EE:02";
        otherSpeaker.SyncCalibrations = cfg.SyncCalibrations;
        Assert.Equal(estimate, Lag(otherSpeaker, new FakeCapture(Speakers)));                 // another speaker
        Assert.Equal(calibrated, Lag(cfg, new FakeCapture(Speakers)));                        // back on the measured path
    }

    [Fact]
    public void A_test_whose_connection_source_or_latency_changed_before_apply_saves_nothing()
    {
        var cfg = Config();
        var context = Context(cfg, new FakeCapture(Speakers));
        var taken = new AvCalibrationSnapshot(context, 200, 0, 20);
        var changes = new AvCalibrationSnapshot?[]
        {
            null,                                                                // the connection dropped (not streaming)
            taken with { Context = Context(Config(), new FakeCapture(Headphones)) }, // another source
            taken with { Context = context with { PerAppRouting = true }, CaptureExtraMs = RoutedCapture.RoutedExtraLatencyMs },
            taken with { EffectiveLatencyMs = 105 },                             // a scene switch reconnected
            taken with { FifoTargetMs = 24 },                                    // a dropout raised the buffer
        };
        foreach (var now in changes)
        {
            Assert.Equal(SyncCalibrationOutcome.PathChanged, TrayApp.StoreSyncCalibration(cfg, taken, now, 260, out _));
            Assert.Empty(cfg.SyncCalibrations);
        }

        // Unchanged: the residual against that path's estimate (200 + 36 = 236) is saved.
        Assert.Equal(SyncCalibrationOutcome.Applied, TrayApp.StoreSyncCalibration(cfg, taken, taken with { }, 260, out int estimate));
        Assert.Equal(236, estimate);
        Assert.Equal(24, AvCalibrations.Get(cfg, context)!.ResidualMs);
        // No correction (too far from the estimate): nothing changes.
        Assert.Equal(SyncCalibrationOutcome.OutOfRange, TrayApp.StoreSyncCalibration(cfg, taken, taken, AvCalibrations.MaxMeasuredMs + 1, out _));
        Assert.Equal(24, AvCalibrations.Get(cfg, context)!.ResidualMs);
    }

    [Fact]
    public async Task One_correction_reaches_home_the_browser_extension_and_mpv_and_vlc_and_scenes_still_change_the_total()
    {
        var cfg = Config();
        var capture = new FakeCapture(Speakers);
        var context = Context(cfg, capture);
        var taken = TrayApp.SnapshotFor(StreamState.Streaming, context, Scenes.MovieMs, 0, 20)!.Value;
        Assert.Equal(SyncCalibrationOutcome.Applied, TrayApp.StoreSyncCalibration(cfg, taken, taken, 250, out int estimate));
        int residual = 250 - estimate;

        var players = new FakeScanner(new FakePlayer("mpv:1", PlayerKind.Mpv, 0), new FakePlayer("vlc:2", PlayerKind.Vlc, 100));
        using var sync = new PlayerSync(players);
        int apiDelay = 0;
        bool apiCalibrated = false;
        using var api = new LocalApi(0, () => TrayApp.ApiStatus(true, cfg.DeviceName, cfg.LatencyMs, apiDelay, apiCalibrated)) { ReadTimeout = TimeSpan.FromSeconds(30) };

        async Task<(int Home, JsonElement Api, int[] Players)> Everywhere(int latencyMs)
        {
            var ctx = TrayApp.CalibrationContextFor(cfg, capture, cfg.CaptureDeviceId);
            int home = LatencyTuner.SoundLagMs(latencyMs, cfg, capture.ExtraLatencyMs, 20, ctx); // 首页's line
            apiDelay = TrayApp.VideoDelayFor(cfg, StreamState.Streaming, latencyMs, capture.ExtraLatencyMs, 20, ctx);
            apiCalibrated = AvCalibrations.Get(cfg, ctx) != null;
            await sync.StepAsync(TrayApp.PlayerInputFor(cfg, StreamState.Streaming, apiDelay), CancellationToken.None);
            return (home, await Status(api.Port), players.Players.Select(p => p.Delay).ToArray());
        }

        var (home, status, delays) = await Everywhere(Scenes.MovieMs);
        Assert.Equal(250, home);                                                      // what the test measured
        Assert.Equal(250, status.GetProperty("videoDelayMs").GetInt32());             // the browser extension
        Assert.Equal("calibrated", status.GetProperty("videoDelaySource").GetString());
        Assert.Equal([-250, -250], delays);                                           // mpv and VLC

        // Another scene's latency (as the slider or a scene button gives it): the correction stays, the total moves.
        (home, status, delays) = await Everywhere(Scenes.MovieMs + 80);
        Assert.Equal(330, home);
        Assert.Equal(330, status.GetProperty("videoDelayMs").GetInt32());
        Assert.Equal([-330, -330], delays);

        // 恢复默认补偿: back to the estimate everywhere.
        Assert.True(AvCalibrations.Reset(cfg, TrayApp.CalibrationContextFor(cfg, capture, cfg.CaptureDeviceId)));
        (home, status, delays) = await Everywhere(Scenes.MovieMs);
        Assert.Equal(estimate, home);
        Assert.Equal(estimate, status.GetProperty("videoDelayMs").GetInt32());
        Assert.Equal("estimate", status.GetProperty("videoDelaySource").GetString());
        Assert.Equal([-estimate, -estimate], delays);
        Assert.NotEqual(0, residual);

        // Out of 影视, the players get their own values back; the API stays.
        cfg.Scene = Scene.Game;
        await sync.StepAsync(TrayApp.PlayerInputFor(cfg, StreamState.Streaming, apiDelay), CancellationToken.None);
        Assert.Equal([0, 100], players.Players.Select(p => p.Delay));
        Assert.Equal(0, TrayApp.VideoDelayFor(cfg, StreamState.Idle, Scenes.MovieMs, 0, 20, context)); // not streaming: 0
    }

    [Fact]
    public void Restoring_the_default_compensation_clears_only_the_path_in_use()
    {
        var cfg = Config();
        var plain = new FakeCapture(Speakers);
        var routed = new FakeCapture(Speakers, RoutedCapture.RoutedExtraLatencyMs);
        foreach (var capture in new[] { plain, routed })
        {
            var snapshot = TrayApp.SnapshotFor(StreamState.Streaming, Context(cfg, capture), 200, capture.ExtraLatencyMs, 20)!.Value;
            Assert.Equal(SyncCalibrationOutcome.Applied, TrayApp.StoreSyncCalibration(cfg, snapshot, snapshot, 280, out _));
        }
        Assert.Equal(2, cfg.SyncCalibrations.Count);

        // TrayApp.ResetSyncCalibration(): the path streaming now is the routed one.
        Assert.True(AvCalibrations.Reset(cfg, TrayApp.CalibrationContextFor(cfg, routed, cfg.CaptureDeviceId)));
        Assert.Null(AvCalibrations.Get(cfg, Context(cfg, routed)));
        Assert.NotNull(AvCalibrations.Get(cfg, Context(cfg, plain)));
        // Nothing usable is captured: nothing to reset.
        Assert.False(AvCalibrations.Reset(cfg, TrayApp.CalibrationContextFor(cfg, new FakeCapture(null, noOutput: true), null)));
        Assert.Single(cfg.SyncCalibrations);
    }

    private static async Task<JsonElement> Status(int port)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET /v1/status HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n\r\n"));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var response = await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.StartsWith("HTTP/1.1 200", response);
        return JsonDocument.Parse(response[(response.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..]).RootElement.Clone();
    }

    private sealed class FakePlayer(string key, PlayerKind kind, int delay) : IPlayerEndpoint
    {
        public string Key { get; } = key;
        public PlayerKind Kind { get; } = kind;
        public string Name => Kind.ToString();
        public int Delay { get; private set; } = delay;
        public Task<PlayerReading> ReadAsync(CancellationToken ct) => Task.FromResult(new PlayerReading(Delay, "item"));

        public Task WriteAsync(int delayMs, CancellationToken ct)
        {
            Delay = delayMs;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeScanner(params FakePlayer[] players) : IPlayerScanner
    {
        public FakePlayer[] Players { get; } = players;
        public Task<PlayerScan> ScanAsync(CancellationToken ct) => Task.FromResult(new PlayerScan(Players, []));
    }
}
