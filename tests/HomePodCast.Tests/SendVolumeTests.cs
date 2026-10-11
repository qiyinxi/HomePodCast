using System.Text.Json;
using HomePodCast.Audio;

namespace HomePodCast.Tests;

public class SendVolumeTests
{
    private static readonly Dictionary<uint, ProcessEntry> Processes = new[]
    {
        new ProcessEntry(10, 0, "browser"),
        new ProcessEntry(11, 10, "browser"),
        new ProcessEntry(12, 10, "browser"),
        new ProcessEntry(20, 0, "game"),
    }.ToDictionary(p => p.Pid);

    [Fact]
    public void Independent_send_activates_routing_even_at_100_and_local_only_keeps_fast_path()
    {
        var rules = RouteRules.Today with { SendVolumes = new Dictionary<string, int> { ["browser"] = 100 } };
        Assert.True(rules.NeedsRouting);
        var plan = RoutePlanner.Plan(rules, [new(11, false), new(12, false), new(20, false)], Processes);
        Assert.Equal(100, plan.Targets.Single(t => t.Key == "browser").SendVolumePercent);
        Assert.Null(plan.Targets.Single(t => t.Key == "game").SendVolumePercent);
        Assert.Equal([new Silence(11, 10), new Silence(12, 10)], plan.SendSessions);

        rules = new RouteRules(AudioRoute.Local, new Dictionary<string, AudioRoute>()) { SendVolumes = rules.SendVolumes };
        Assert.Empty(RoutePlanner.Plan(rules, [new(11, false)], Processes).Targets);
        rules = new RouteRules(AudioRoute.Both, new Dictionary<string, AudioRoute> { ["browser"] = AudioRoute.Local })
            { SendVolumes = rules.SendVolumes };
        Assert.True(rules.NeedsRouting); // its Local destination itself needs filtering
    }

    [Fact]
    public void Send_levels_follow_the_captured_ancestor_and_are_part_of_plan_changes()
    {
        var rules = RouteRules.Today with
        {
            SendVolumes = new Dictionary<string, int> { ["browser"] = 60 },
        };
        var a = RoutePlanner.Plan(rules, [new(11, false)], Processes);
        var b = RoutePlanner.Plan(rules with { SendVolumes = new Dictionary<string, int> { ["browser"] = 20 } },
            [new(11, false)], Processes);
        Assert.False(a.SameAs(b));
        Assert.True(a.SameAs(RoutePlanner.Plan(rules, [new(11, false)], Processes)));
        Assert.False(a.SameAs(RoutePlanner.Plan(rules, [new(11, false), new(12, false)], Processes)));
    }

    [Fact]
    public void Independent_send_60_with_windows_20_produces_60_percent_copy()
    {
        var gain = new SendVolumeGain(settleDelay: 100, verifyWindow: 100);
        gain.Verify(10, 0.2f, 0);
        gain.Verify(10, 0.2f, 100);
        var mix = new StreamMixer(100);
        mix.Add(10);
        mix.Push(10, [0.5f * 0.2f, 0.5f * 0.2f], 0.6f * gain.For(10, 100));
        var output = new List<float>();
        Assert.Equal(1, mix.Mix(output));
        Assert.All(output, sample => Assert.Equal(0.3f, sample, 5));
        // The local copy is still 0.1: only the captured copy gets the HomePod send gain.
        Assert.Equal(5f, gain.For(10, 100));
    }

    [Fact]
    public void Independent_send_also_undoes_homepod_only_attenuation_after_its_guard()
    {
        var gain = new SendVolumeGain(100, 100);
        float rawLevel = SessionAttenuation.Attenuated(0.2f);
        gain.Verify(10, SessionAttenuation.ToLogical(rawLevel), 0);
        gain.Verify(10, SessionAttenuation.ToLogical(rawLevel), 100);
        float rawSample = 0.5f * rawLevel;
        float compensation = SessionAttenuation.CompensationGain(true, true, rawSample);
        Assert.Equal(0.3f, rawSample * compensation * gain.For(10, 100) * 0.6f, 5);
        Assert.Equal(0.2f, SessionAttenuation.Restored(rawLevel), 5); // restore still returns Windows level
    }

    [Fact]
    public void Level_changes_and_stale_checks_disable_inverse_until_settled_and_verified()
    {
        var gain = new SendVolumeGain(100, 100);
        gain.Verify(10, 0.2f, 0);
        Assert.Equal(1f, gain.For(10, 99));
        gain.Verify(10, 0.2f, 100);
        Assert.Equal(5f, gain.For(10, 100));
        gain.Changed(10, 110); // event arrives before captured audio at the new Windows volume
        Assert.Equal(1f, gain.For(10, 111));
        gain.Verify(10, 1f, 115);
        Assert.Equal(1f, gain.For(10, 215));
        gain.Verify(10, 0.1f, 220);
        Assert.Equal(1f, gain.For(10, 319));
        gain.Verify(10, 0.1f, 320);
        Assert.Equal(10f, gain.For(10, 320));
        Assert.Equal(1f, gain.For(10, 421));
        gain.Clear(); // capture device switch / restore
        Assert.Equal(1f, gain.For(10, 320));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.001f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Zero_tiny_or_invalid_session_levels_are_never_amplified(float level)
    {
        var gain = new SendVolumeGain(100, 100);
        gain.Verify(1, level, 0);
        gain.Verify(1, level, 100);
        Assert.Equal(1f, gain.For(1, 100));
    }

    [Fact]
    public void Mismatched_sessions_and_missing_sessions_fall_back_without_inverse()
    {
        Assert.Equal(0.2f, SendVolumeGain.CommonLevel([0.2f, 0.2f]));
        Assert.Null(SendVolumeGain.CommonLevel([0.2f, 0.7f]));
        Assert.Null(SendVolumeGain.CommonLevel([0.2f, 0f]));
        Assert.Null(SendVolumeGain.CommonLevel([]));
        var gain = new SendVolumeGain(100, 100);
        gain.Verify(10, 0.2f, 0);
        gain.Verify(10, null, 100); // muted, missing or incompatible sessions
        Assert.Equal(1f, gain.For(10, 200));
        gain.KeepOnly(new HashSet<uint>());
        Assert.Equal(1f, gain.For(10, 100));
    }

    [Fact]
    public void Unexpected_loud_packets_never_receive_an_extreme_inverse()
    {
        Assert.Equal(5f, SendVolumeGain.Guard(5f, 0.1f));
        Assert.Equal(1f, SendVolumeGain.Guard(100f, 0.8f)); // old packet after Windows drops to 1 %
        Assert.Equal(1f, SendVolumeGain.Guard(float.PositiveInfinity, 0.1f));
        var mixer = new StreamMixer(100);
        mixer.Add(1);
        mixer.Push(1, [2f, -2f], gain: 100f);
        var output = new List<float>();
        mixer.Mix(output);
        Assert.Equal([1f, -1f], output); // the final digital mix is clipped even for an over-range app
    }

    [Fact]
    public void Independent_send_config_is_normalized_persisted_and_resettable()
    {
        var config = new AppConfig();
        int saves = 0, changes = 0;
        var routing = new AppRouting(config, () => saves++);
        routing.Changed += () => changes++;
        routing.SetSendVolume(" Browser.EXE ", 60);
        var snapshot = routing.Rules;
        Assert.Equal(60, config.AppSendVolumes["browser"]);
        Assert.True(snapshot.NeedsRouting);
        routing.SetSendVolume("browser", 60);
        Assert.Equal(1, saves);
        routing.SetSendVolume("browser", 999);
        Assert.Equal(100, routing.GetSendVolume("browser"));
        Assert.True(routing.Rules.NeedsRouting);
        Assert.Equal(60, snapshot.SendVolumes["browser"]);
        var persisted = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config))!;
        Assert.Equal(100, persisted.AppSendVolumes["browser"]);
        Assert.Empty(JsonSerializer.Deserialize<AppConfig>("{}")!.AppSendVolumes);
        routing.SetSendVolume("browser", null);
        Assert.False(routing.Rules.NeedsRouting);
        Assert.Null(routing.GetSendVolume("browser"));
        Assert.Equal(3, saves);
        Assert.Equal(3, changes);
    }

    [Fact]
    public void Reload_switches_capture_choice_and_routing_snapshot_without_saving()
    {
        var config = new AppConfig();
        int saves = 0, changes = 0;
        var routing = new AppRouting(config, () => saves++);
        routing.Changed += () => changes++;
        config.AppSendVolumes["game"] = 60;
        config.CaptureDeviceId = " new-device ";
        routing.Reload();
        Assert.Equal("new-device", routing.CaptureDeviceId);
        Assert.Equal(60, routing.GetSendVolume("game"));
        Assert.Equal(0, saves);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Sync_test_scope_preserves_capture_mode_and_restores_current_configuration()
    {
        if (!AppRouting.Supported) return;
        string own = RouteRules.KeyFor(System.Diagnostics.Process.GetCurrentProcess().ProcessName);
        var config = new AppConfig();
        config.AppRoutes[own] = AudioRoute.Local; // replacing the only nondefault rule must retain routed mode
        int saves = 0;
        var routing = new AppRouting(config, () => saves++);
        using (var test = routing.BeginSyncTestRoute())
        {
            Assert.True(routing.Active);
            Assert.Equal(AudioRoute.HomePod, routing.Rules.For(own));
            Assert.Equal(AudioRoute.Local, config.AppRoutes[own]);
            routing.SetSendVolume("browser", 60); // a user edit during a test must survive restoration
            test.Dispose();
            Assert.Equal(AudioRoute.Local, routing.Rules.For(own));
            Assert.Equal(60, routing.GetSendVolume("browser"));
        }
        Assert.Equal(1, saves); // only the deliberate edit, never the temporary route
        var endpoint = new AppRouting(new AppConfig(), () => saves++);
        using (endpoint.BeginSyncTestRoute()) Assert.False(endpoint.Rules.NeedsRouting);
    }
}
