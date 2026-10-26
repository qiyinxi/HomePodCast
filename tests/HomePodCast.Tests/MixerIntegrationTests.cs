using System.Net;
using System.Text.Json;
using HomePodCast.Audio;
using HomePodCast.Net;
using HomePodCast.Players;
using HomePodCast.UI;

namespace HomePodCast.Tests;

public sealed class MixerIntegrationTests
{
    [Fact]
    public async Task Local_API_uses_the_requested_app_and_never_a_global_maximum()
    {
        using var api = new LocalApi(0, app => new { app = "HomePodCast", destination = app, videoDelayMs = app == "game" ? 140 : 2035 });
        using var http = new HttpClient();
        using var game = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{api.Port}/v1/status?app=Game.exe"));
        Assert.Equal("game", game.RootElement.GetProperty("destination").GetString());
        Assert.Equal(140, game.RootElement.GetProperty("videoDelayMs").GetInt32());
        Assert.Null(LocalApi.AppKeyFromHead("GET /v1/status?app=game&app=music HTTP/1.1\r\n"));
        Assert.Null(LocalApi.AppKeyFromHead("GET /v1/status?app=..%2Fgame HTTP/1.1\r\n"));
    }

    [Fact]
    public async Task Players_follow_their_own_destinations_and_restore_only_the_unavailable_one()
    {
        var game = new Player("game", 12); var music = new Player("music", 24);
        using var sync = new PlayerSync(new Scanner([game, music]));
        var input = new PlayerSyncInput(true, StreamState.Streaming, 9999)
        { ForEndpoint = e => new(true, StreamState.Streaming, e.Key == "game" ? 140 : 2035) };
        await sync.StepAsync(input, CancellationToken.None);
        Assert.Equal(-140, game.Delay); Assert.Equal(-2035, music.Delay);
        await sync.StepAsync(input with { ForEndpoint = e => e.Key == "game" ? new(true, StreamState.Streaming, 140) : null }, CancellationToken.None);
        Assert.Equal(-140, game.Delay); Assert.Equal(24, music.Delay);
        Assert.Contains(sync.Statuses, s => s.Name == "music" && s.State == PlayerState.DestinationRequired);
        Assert.Null(sync.TargetMs);
    }

    [Fact]
    public void Nested_players_resolve_to_the_captured_outer_root()
    {
        using var router = new SessionRouter(new AppRouting(new AppConfig(), () => { }), _ => false);
        router.UsePlan(new(false, [new(1, "launcher", false)], []), new Dictionary<uint, ProcessEntry>
        { [1] = new(1, 0, "launcher"), [2] = new(2, 1, "vlc") });
        Assert.Equal("launcher", router.OwnerKeyFor(2));
        Assert.Null(router.OwnerKeyFor(123));
    }

    [Fact]
    public async Task Active_group_edits_are_snapshots_until_explicit_application()
    {
        var h = new MixerCoordinatorTests.Harness(); await using var mixer = h.Coordinator;
        var config = MixerCoordinatorTests.Config(); MixerCoordinatorTests.Routes(config);
        await mixer.ConnectAsync(config, ConnectTrigger.Manual);
        await mixer.RefreshRootsAsync([new(2, "music", false)]);
        var music = h.Sessions["group:music"];
        config.SavedOutputGroups.Single().Settings.GroupDelayMs = 30000;
        await mixer.RefreshRootsAsync([new(2, "music", false)]);
        Assert.Same(music, h.Sessions["group:music"]); Assert.Equal(1, music.Starts);
        await mixer.ConnectAsync(config, ConnectTrigger.Automatic);
        Assert.NotSame(music, h.Sessions["group:music"]);
        Assert.Equal(1, music.Stops);
    }

    [Fact]
    public async Task Calibration_prevents_replacing_its_destination_and_file_playback_shares_the_leases()
    {
        var h = new MixerCoordinatorTests.Harness(); await using var mixer = h.Coordinator;
        var config = MixerCoordinatorTests.Config(); MixerCoordinatorTests.Routes(config);
        await mixer.ConnectAsync(config, ConnectTrigger.Manual);
        mixer.Select("speaker:airplay:AABBCCDDEEFF");
        using (mixer.BeginCalibrationRoute(mixer.SelectedKey))
        {
            await Assert.ThrowsAsync<MixerLeaseException>(() => mixer.AcquireExternalAsync("file", ["airplay:AABBCCDDEEFF"], true));
            var settings = MixerRoutes.Read(config).Settings!;
            settings.DestinationSettings["speaker:airplay:AABBCCDDEEFF"] = new(Scene.Custom, 300);
            // Current still follows its global request. Changing that request is rejected while measuring.
            var changed = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config))!; changed.LatencyMs = 300;
            Assert.Equal(MixerEditResult.Conflict, mixer.ValidateConfiguration(changed).Result);
        }
        using var file = await mixer.AcquireExternalAsync("file", ["airplay:AABBCCDDEEFF"], true);
        Assert.Empty(mixer.Statuses);
        Assert.Equal(1, mixer.Leases.Count);
        Assert.Equal(MixerEditResult.Conflict, (await mixer.ConnectAsync(config, ConnectTrigger.Manual)).Result);
    }

    [Fact]
    public void Profiles_roundtrip_V2_without_starting_any_capture_and_unknown_formats_refuse_lossy_apply()
    {
        var config = MixerCoordinatorTests.Config(); var settings = MixerCoordinatorTests.Routes(config);
        var saved = UsageProfiles.Capture(config);
        var other = MixerCoordinatorTests.Config();
        UsageProfiles.Apply(other, saved);
        Assert.Equal(settings.Apps["music"], MixerRoutes.Read(other).Settings!.Apps["music"]);
        saved.MixerRouting = JsonDocument.Parse("{\"Version\":77}").RootElement.Clone();
        string before = JsonSerializer.Serialize(other);
        Assert.Throws<InvalidOperationException>(() => UsageProfiles.Apply(other, saved));
        Assert.Equal(before, JsonSerializer.Serialize(other));
    }

    [Fact]
    public void Destination_and_capture_mode_are_part_of_the_calibration_key()
    {
        var config = MixerCoordinatorTests.Config();
        var path = AvCalibrationContext.For(config, "fake-endpoint", false)!.Value;
        var a = path with { MixerDestinationId = "group:a" };
        var b = path with { MixerDestinationId = "group:b" };
        Assert.NotEqual(a.Key, b.Key); Assert.NotEqual(a.Key, (a with { PerAppRouting = true }).Key);
        Assert.NotEqual(a.Key, path.Key);
    }

    [Fact]
    public void Pair_and_single_speaker_overlap_is_detected_before_any_session_opens()
    {
        AirPlayDevice Member(string id) => new(id, id, IPAddress.Loopback, 7000, "AudioAccessory6,1", new Dictionary<string, string> { ["tsid"] = "room" });
        var config = MixerCoordinatorTests.Config(); config.DeviceId = "pair:room";
        var destination = MixerDestinationResolver.Resolve(config, MixerRoute.Current);
        var outputs = MixerDeviceIdentities.Outputs(destination, [Member("AABBCCDDEEFF"), Member("112233445566")])!;
        var leases = new MixerDeviceLeaseTable(); using var single = leases.Acquire("single", ["airplay:AABBCCDDEEFF"]);
        Assert.Throws<MixerLeaseException>(() => leases.Acquire("pair", outputs));
        Assert.Equal(1, leases.Count);
    }

    [Fact]
    public void One_hour_of_music_clock_drift_pause_and_retry_does_not_change_the_game_timeline()
    {
        // Scale to 1 kHz to simulate an hour deterministically, preserving exact millisecond/frame arithmetic.
        var game = new TimelineBuffer(1000, 5000, 0); var alone = new TimelineBuffer(1000, 5000, 0);
        var music = new TimelineBuffer(1000, 5000, 0);
        var signal = new float[200]; var expected = new float[200]; var actual = new float[200]; var slow = new float[200];
        for (int cycle = 0; cycle < 36000; cycle++)
        {
            Array.Fill(signal, (cycle % 13 + 1) / 20f);
            int frame = cycle * 100;
            game.WriteFrames(frame, signal); alone.WriteFrames(frame, signal); music.WriteFrames(frame, signal);
            long gameDue = game.TicksAt(frame + 105 + RoutedCapture.RoutedExtraLatencyMs);
            Assert.True(game.ReadAt(gameDue, 105, RoutedCapture.RoutedExtraLatencyMs, actual));
            Assert.True(alone.ReadAt(gameDue, 105, RoutedCapture.RoutedExtraLatencyMs, expected));
            // The music renderer may stop, retry or read on a clock drifting by +1000 ppm. Its reader never owns game history.
            if (cycle % 1000 > 100) music.ReadAt(music.TicksAt(frame + frame / 1000 + 2000 + RoutedCapture.RoutedExtraLatencyMs), 2000,
                RoutedCapture.RoutedExtraLatencyMs, slow);
            Assert.Equal(expected, actual);
        }
        Assert.Equal(3_600_000, game.EndFrame);
    }

    private sealed class Player(string key, int original) : IPlayerEndpoint
    {
        public int Delay = original;
        public string Key => key;
        public string Name => key;
        public PlayerKind Kind => PlayerKind.Mpv;
        public Task<PlayerReading> ReadAsync(CancellationToken ct) => Task.FromResult(new PlayerReading(Delay));
        public Task WriteAsync(int delayMs, CancellationToken ct) { Delay = delayMs; return Task.CompletedTask; }
    }
    private sealed class Scanner(IReadOnlyList<IPlayerEndpoint> endpoints) : IPlayerScanner
    { public Task<PlayerScan> ScanAsync(CancellationToken ct) => Task.FromResult(new PlayerScan(endpoints, [])); }
}
