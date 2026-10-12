using System.Text.Json;
using HomePodCast.Audio;
using HomePodCast.UI.Pages;

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

    private static SessionLevel At(float level, bool muted = false) => new(level, muted);

    private static SendCheck Check(params SessionLevel[] sessions) => SendVolumeGain.Evaluate(sessions, complete: true);

    private static float[] Frames(int n, float value) => Enumerable.Repeat(value, n * 2).ToArray();

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
    public void Send_levels_follow_the_captured_ancestor_and_only_turning_them_on_or_off_changes_the_plan()
    {
        var rules = RouteRules.Today with
        {
            SendVolumes = new Dictionary<string, int> { ["browser"] = 60 },
        };
        var a = RoutePlanner.Plan(rules, [new(11, false)], Processes);
        var b = RoutePlanner.Plan(rules with { SendVolumes = new Dictionary<string, int> { ["browser"] = 20 } },
            [new(11, false)], Processes);
        Assert.Equal(20, b.Targets.Single().SendVolumePercent);
        // A level that only moved is applied live: no plan-change log line, nothing re-opened or re-checked.
        Assert.True(a.SameAs(b));
        Assert.False(a.Targets.SequenceEqual(b.Targets)); // the router still takes the new plan, silently
        Assert.True(a.SameAs(RoutePlanner.Plan(rules, [new(11, false)], Processes)));
        Assert.False(a.SameAs(RoutePlanner.Plan(rules, [new(11, false), new(12, false)], Processes)));

        // Turned off while another rule keeps per-app routing on: a different capture (its sessions are no longer checked).
        var local = new Dictionary<string, AudioRoute> { ["game"] = AudioRoute.Local };
        var on = RoutePlanner.Plan(rules with { Apps = local }, [new(11, false)], Processes);
        var off = RoutePlanner.Plan(rules with { Apps = local, SendVolumes = new Dictionary<string, int>() }, [new(11, false)], Processes);
        Assert.False(on.SameAs(off));
        Assert.False(off.SameAs(on));
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

    /// <summary>
    /// Device checklist 「两者」: an app heard here at its Windows level and sent to the HomePod at its own 60 %. Process
    /// loopback captures it after the Windows session volume, so the capture divides that level out again.
    /// </summary>
    [Fact]
    public void Both_mode_keeps_the_homepod_at_its_own_60_percent_while_windows_goes_from_20_to_40_percent()
    {
        const float source = 0.5f; // what the app plays before its Windows volume
        var gain = new SendVolumeGain(settleDelay: 100, verifyWindow: 100);
        float Here(float windows) => source * windows;
        float HomePod(float windows, long now)
        {
            float captured = source * windows;
            var g = RoutedCapture.ComposeGain(60, false, false, gain.For(10, now), captured);
            return captured * g.Gain * g.Level;
        }

        gain.Verify(10, Check(At(0.2f)), 0);
        gain.Verify(10, Check(At(0.2f)), 90);
        Assert.Equal(0.3f, HomePod(0.2f, 95), 5);                   // 60 % of 0.5
        Assert.Equal(0.1f, Here(0.2f), 5);

        // The Windows mixer: 20 → 40 %. Its event arrives before the louder audio reaches the capture.
        gain.Changed(10, 100, 0.4f);
        Assert.Equal(0.3f, HomePod(0.4f, 101), 5);                  // the new audio: exactly 60 % at once
        Assert.True(HomePod(0.2f, 101) <= 0.3f + 1e-6f);            // audio still on its way at 20 %: quieter, never louder
        gain.Verify(10, Check(At(0.4f)), 110);
        gain.Verify(10, Check(At(0.4f)), 205);
        Assert.Equal(0.3f, HomePod(0.4f, 250), 5);                  // settled: the HomePod sounds the same...
        Assert.Equal(0.2f, Here(0.4f), 5);                          // ...while this PC got twice as loud

        // Muted, or at 0 %, in Windows: nothing is captured, so both are silent (and nothing is divided out).
        foreach (var silent in new[] { At(0.4f, muted: true), At(0f) })
        {
            var check = Check(silent);
            Assert.Equal(SendVolumeStatus.MutedOrZero, check.Status);
            gain.Verify(10, check, 300);
            Assert.Equal(1f, gain.For(10, 301));
            float captured = silent.Muted ? 0f : source * silent.Logical;
            var g = RoutedCapture.ComposeGain(60, false, false, gain.For(10, 301), captured);
            Assert.Equal(0f, captured * g.Gain * g.Level);
        }

        // Below 1 %, or the app's sessions at different levels: no inverse, the HomePod just follows Windows (60 % x
        // Windows), quieter than set and never louder.
        foreach (var (check, windows) in new[] { (Check(At(0.005f)), 0.005f), (Check(At(0.2f), At(0.7f)), 0.7f) })
        {
            gain.Verify(20, check, 400);
            Assert.Equal(1f, gain.For(20, 401));
            Assert.True(HomePodAt(20, windows, 401) <= 0.6f * source * windows + 1e-6f);
        }

        float HomePodAt(uint root, float windows, long now)
        {
            float captured = source * windows;
            var g = RoutedCapture.ComposeGain(60, false, false, gain.For(root, now), captured);
            return captured * g.Gain * g.Level;
        }
    }

    [Fact]
    public void A_level_change_uses_the_higher_of_the_old_and_new_level_until_it_has_reached_the_capture()
    {
        var gain = new SendVolumeGain(settleDelay: 100, verifyWindow: 100);
        gain.Verify(10, 0.2f, 0);
        Assert.Equal(5f, gain.For(10, 0));           // the first check: the audio in flight is at the level it read
        // The Windows slider goes up to 25 %: its event arrives before that audio reaches the capture.
        gain.Changed(10, 10, 0.25f);
        Assert.Equal(4f, gain.For(10, 11));          // 1/max(20 %, 25 %): 20 % audio comes out 0.8x, 25 % audio exact
        gain.Verify(10, 0.25f, 20);
        Assert.Equal(4f, gain.For(10, 50));
        Assert.Equal(4f, gain.For(10, 119));
        gain.Verify(10, 0.25f, 120);
        Assert.Equal(4f, gain.For(10, 121));
        // Down to 10 %: the 25 % audio still on its way keeps the inverse at 4 for the settle delay, then 10.
        gain.Changed(10, 200, 0.1f);
        gain.Verify(10, 0.1f, 205);
        Assert.Equal(4f, gain.For(10, 210));
        Assert.Equal(4f, gain.For(10, 299));
        gain.Verify(10, 0.1f, 300);
        Assert.Equal(4f, gain.For(10, 304));         // the check at 205 covers 25 % until 305
        Assert.Equal(10f, gain.For(10, 305));
        // No check within the verify window (a stalled router): no inverse at all.
        Assert.Equal(1f, gain.For(10, 401));
        gain.Clear(); // capture device switch / restore
        Assert.Equal(1f, gain.For(10, 300));
    }

    [Fact]
    public void A_volume_event_covers_the_reported_level_until_a_check_that_began_after_it_has_read_it()
    {
        var gain = new SendVolumeGain(100, 100);
        gain.Verify(10, 0.2f, 0);
        gain.Changed(10, 10, 0.8f);                     // raised to 80 % in the Windows mixer
        Assert.Equal(1f / 0.8f, gain.For(10, 11));      // 80 % audio exact, the 20 % audio still on its way quieter
        gain.Verify(10, 0.2f, 9);                       // read just before the event: the old level, the event still counts
        Assert.Equal(1f / 0.8f, gain.For(10, 105));
        gain.Verify(10, 0.8f, 106);                     // read after it
        Assert.Equal(1f / 0.8f, gain.For(10, 200));
        // Back down to 20 %: the 80 % audio still on its way keeps 1/0.8 for the settle delay, then 5.
        gain.Changed(10, 200, 0.2f);
        gain.Verify(10, 0.2f, 201);
        gain.Verify(10, 0.2f, 290);
        Assert.Equal(1f / 0.8f, gain.For(10, 300));
        Assert.Equal(5f, gain.For(10, 301));
        // An event whose level is not known: as if at full level until a check has read it.
        gain.Verify(10, 0.2f, 395);
        gain.Changed(10, 400);
        Assert.Equal(1f, gain.For(10, 401));
    }

    [Fact]
    public void The_router_hands_volume_events_of_a_send_target_to_the_normalizer()
    {
        using var router = new SessionRouter(new AppRouting(new AppConfig(), () => { }), _ => true);
        router.UsePlan(new RoutePlan(false, [new RouteTarget(7, "game", Compensate: false) { SendVolumePercent = 60 }], []));
        long t = SessionRouter.ConfirmDelay * 10;
        router.SendGain.Verify(7, 0.2f, t);
        Assert.Equal(5f, router.SendNormalization(7, t));
        // Our own write keeps the logical level: nothing changes.
        router.OnSessionVolume(7, SessionAttenuation.Attenuated(0.2f), SessionRouter.OwnContext, t + 1);
        Assert.Equal(5f, router.SendNormalization(7, t + 2), 3);
        // Raised to 50 % in the Windows mixer: covered at once, before that audio reaches the capture.
        router.OnSessionVolume(7, 0.5f, Guid.NewGuid(), t + 3);
        Assert.Equal(2f, router.SendNormalization(7, t + 4));
        Assert.Equal(2f, router.SendNormalization(7, t + SessionRouter.ConfirmDelay)); // no check has read it yet
        Assert.False(router.IsConfirmed(7, t + 4)); // a "两者" target: the compensation gate is not involved
        // A target without its own level is left alone.
        router.OnSessionVolume(8, 0.9f, Guid.NewGuid(), t + 5);
        Assert.Equal(1f, router.SendNormalization(8, t + 6));
    }

    [Fact]
    public void A_session_joining_a_send_target_is_covered_at_its_level_and_an_untracked_one_leaves_the_inverse_off()
    {
        var gain = new SendVolumeGain(100, 100);
        gain.Verify(10, 0.2f, 0);
        // The router starts tracking a new session of the app (silenced here, or only watched): its audio may
        // already be on its way, at its own level.
        gain.Changed(10, 10, 1f);
        Assert.Equal(1f, gain.For(10, 11));

        // The plan sends two sessions through root 10: until both are tracked the check is incomplete.
        var sends = new[] { new Silence(11, 10), new Silence(12, 10), new Silence(21, 20) };
        Assert.False(SessionRouter.Covers(sends, 10, [new Silence(11, 10)]));
        Assert.False(SessionRouter.Covers(sends, 10, [new Silence(11, 10), new Silence(12, 20)])); // tracked for another target
        Assert.True(SessionRouter.Covers(sends, 10, [new Silence(12, 10), new Silence(11, 10)]));
        Assert.True(SessionRouter.Covers(sends, 30, []));
        gain.Verify(10, SendVolumeGain.Evaluate([At(0.2f)], complete: false), 20);
        Assert.Equal(1f, gain.For(10, 21));
        // Both tracked and read, the new one at 100 %: different levels, still no inverse.
        gain.Verify(10, Check(At(0.2f), At(1f)), 30);
        Assert.Equal(1f, gain.For(10, 31));
        // The new session brought down to 20 %: its louder audio still on its way is covered, then 5.
        gain.Verify(10, Check(At(0.2f), At(0.2f)), 40);
        Assert.Equal(1f, gain.For(10, 41));
        Assert.Equal(5f, gain.For(10, 140));
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
    public void Checks_map_sessions_to_statuses_and_only_a_common_level_of_at_least_1_percent_is_divided_out()
    {
        Assert.Equal(new SendCheck(SendVolumeStatus.Available, 0.2f, 0.2f), Check(At(0.2f), At(0.2f)));
        // Muted and 0 % sessions are silent: left out (their volume event announces them before they are heard).
        Assert.Equal(new SendCheck(SendVolumeStatus.Available, 0.2f, 0.2f), Check(At(0.2f), At(0.9f, muted: true), At(0f)));
        Assert.Equal(new SendCheck(SendVolumeStatus.MutedOrZero, null, 0f), Check(At(0.4f, muted: true), At(0f)));
        Assert.Equal(new SendCheck(SendVolumeStatus.DifferentSessionLevels, null, 0.7f), Check(At(0.2f), At(0.7f)));
        Assert.Equal(new SendCheck(SendVolumeStatus.BelowMinimum, null, 0.005f), Check(At(0.005f)));
        Assert.Equal(new SendCheck(SendVolumeStatus.BelowMinimum, null, 0.7f), Check(At(0.005f), At(0.2f), At(0.7f)));
        // Nothing read yet, a session the plan sends that is not tracked, a failed read, a broken level.
        Assert.Equal(new SendCheck(SendVolumeStatus.Waiting, null, 1f), Check());
        Assert.Equal(new SendCheck(SendVolumeStatus.Waiting, null, 1f), SendVolumeGain.Evaluate([At(0.2f)], complete: false));
        Assert.Equal(new SendCheck(SendVolumeStatus.Waiting, null, 1f), Check(At(0.2f), At(float.NaN)));

        var gain = new SendVolumeGain(100, 100);
        gain.Verify(10, Check(At(0.2f), At(0.7f)), 0);
        Assert.Equal(1f, gain.For(10, 0));               // different levels: Windows level x send, quieter than set
        gain.Verify(20, Check(At(0.5f, muted: true)), 0);
        Assert.Equal(1f, gain.For(20, 0));
        gain.Verify(30, Check(At(0.005f)), 0);
        Assert.Equal(1f, gain.For(30, 0));
        gain.Verify(40, SendVolumeGain.Evaluate([At(0.2f)], complete: false), 0);
        Assert.Equal(1f, gain.For(40, 0));
        // The louder session brought down to the other's level: until its audio has passed, the inverse covers it.
        gain.Verify(10, Check(At(0.2f), At(0.2f)), 50);
        Assert.Equal(1f / 0.7f, gain.For(10, 60));
        Assert.Equal(5f, gain.For(10, 150));
        gain.KeepOnly(new HashSet<uint>());
        Assert.Equal(1f, gain.For(10, 150));
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
    public void Without_an_own_homepod_level_a_packet_gets_exactly_the_old_gain()
    {
        float quiet = 0.5f * SessionAttenuation.Epsilon, loud = 0.5f;
        // Sent and heard here ("两者", or HomePod while the whole output is not split): as captured.
        Assert.Equal(new PacketGain(1f, 1f), RoutedCapture.ComposeGain(null, false, false, 1f, loud));
        Assert.Equal(new PacketGain(1f, 1f), RoutedCapture.ComposeGain(null, false, false, 5f, loud)); // no normalization
        // "HomePod only": 1/epsilon once confirmed silenced, for attenuated audio only.
        Assert.Equal(new PacketGain(SessionAttenuation.Gain, 1f), RoutedCapture.ComposeGain(null, true, true, 1f, quiet));
        Assert.Equal(new PacketGain(1f, 1f), RoutedCapture.ComposeGain(null, true, false, 1f, quiet));
        Assert.Equal(new PacketGain(1f, 1f), RoutedCapture.ComposeGain(null, true, true, 1f, loud));
    }

    [Fact]
    public void With_an_own_homepod_level_a_packet_gets_send_times_normalization_and_every_fallback_is_quieter()
    {
        // Windows 20 %, HomePod 60 %: audio 0.5 is captured at 0.1 and sent at 0.3.
        var both = RoutedCapture.ComposeGain(60, false, false, 5f, 0.1f);
        Assert.Equal(new PacketGain(1f, 0.6f * 5f), both);
        Assert.Equal(0.3f, 0.1f * both.Gain * both.Level, 5);
        var homePodOnly = RoutedCapture.ComposeGain(60, true, true, 5f, 0.1f * SessionAttenuation.Epsilon);
        Assert.Equal(new PacketGain(SessionAttenuation.Gain, 0.6f * 5f), homePodOnly);
        Assert.Equal(0.3f, 0.1f * SessionAttenuation.Epsilon * homePodOnly.Gain * homePodOnly.Level, 4);
        Assert.Equal(0f, RoutedCapture.ComposeGain(0, false, false, 5f, 0.1f).Level);
        Assert.Equal(5f, RoutedCapture.ComposeGain(150, false, false, 5f, 0.1f).Level);

        // Fallbacks: no inverse (muted or 0 %, below 1 %, different levels, not checked: normalization 1), a
        // "HomePod only" app not confirmed silenced or with audio that is not attenuated, and audio louder than the
        // normalization allows. At any Windows level the HomePod gets at most the 60 % that was set.
        var fallbacks = new[]
        {
            RoutedCapture.ComposeGain(60, false, false, 1f, 0.5f),
            RoutedCapture.ComposeGain(60, true, false, 5f, 0.1f),
            RoutedCapture.ComposeGain(60, true, true, 5f, 0.3f),
            RoutedCapture.ComposeGain(60, false, false, 100f, 0.8f),
            RoutedCapture.ComposeGain(60, false, false, float.NaN, 0.1f),
        };
        Assert.Equal(new PacketGain(1f, 0.6f), fallbacks[0]);
        Assert.All(fallbacks.Skip(1), g => Assert.Equal(new PacketGain(1f, 0.6f, Snap: true), g)); // applied at once
        foreach (var g in fallbacks)
            foreach (float windows in new[] { 1f, 0.5f, 0.2f, 0.01f, 0.005f, 0f })
                Assert.True(windows * g.Gain * g.Level <= 0.6f + 1e-6f, $"{g} at Windows {windows}");

        // While a change settles: 20 % -> 25 % uses 1/25 %, right for the new audio, quieter for the old.
        var gain = new SendVolumeGain(100, 100);
        gain.Verify(10, 0.2f, 0);
        gain.Changed(10, 10, 0.25f);
        var settling = RoutedCapture.ComposeGain(60, false, false, gain.For(10, 11), 0.5f * 0.2f);
        Assert.Equal(new PacketGain(1f, 0.6f * 4f), settling);
        Assert.True(0.2f * settling.Level <= 0.6f + 1e-6f);
        Assert.Equal(0.6f, 0.25f * settling.Level, 5);
    }

    [Fact]
    public void A_level_ramps_across_the_packet_and_a_gain_applies_to_its_packet_only()
    {
        var mixer = new StreamMixer(1000);
        mixer.Add(1);
        var output = new List<float>();
        float[] Push(int frames, float value, PacketGain packet)
        {
            output.Clear();
            mixer.Push(1, Frames(frames, value), packet);
            mixer.Mix(output);
            return output.ToArray();
        }

        Assert.All(Push(4, 0.25f, new PacketGain(1f, 2f)), s => Assert.Equal(0.5f, s)); // the first packet: no ramp
        // Level 2 -> 1 across 4 frames: 1.75, 1.5, 1.25, 1 (no step at the packet boundary).
        Assert.Equal([0.4375f, 0.4375f, 0.375f, 0.375f, 0.3125f, 0.3125f, 0.25f, 0.25f], Push(4, 0.25f, new PacketGain(1f, 1f)));
        Assert.Equal([0.375f, 0.375f, 0.5f, 0.5f], Push(2, 0.25f, new PacketGain(1f, 2f)));    // and up again
        // Audio already louder than the level allows: down at once; a higher level still ramps.
        Assert.All(Push(2, 0.25f, new PacketGain(1f, 0.5f, Snap: true)), s => Assert.Equal(0.125f, s));
        Assert.Equal([0.1875f, 0.1875f, 0.25f, 0.25f], Push(2, 0.25f, new PacketGain(1f, 1f, Snap: true)));
        // 1/epsilon undoes the attenuation of its own packet; the next, unattenuated one is never ramped from it.
        Assert.All(Push(2, SessionAttenuation.Epsilon * 0.5f, new PacketGain(SessionAttenuation.Gain, 1f)), s => Assert.Equal(0.5f, s, 4));
        Assert.All(Push(2, 0.5f, new PacketGain(1f, 1f)), s => Assert.Equal(0.5f, s));
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
        Assert.Equal(2, changes); // on and off change the capture; a level that only moved does not
    }

    [Fact]
    public void Moving_the_homepod_slider_is_live_at_once_and_saved_once_without_a_new_plan()
    {
        var config = new AppConfig();
        int saves = 0, changes = 0;
        var routing = new AppRouting(config, () => saves++);
        routing.Changed += () => changes++;
        routing.SetSendVolume("game", 20);
        Assert.Equal((1, 1), (saves, changes));
        for (int v = 21; v <= 40; v++) routing.PreviewSendVolume("Game.exe", v); // one call per Scroll tick
        Assert.Equal(40, routing.GetSendVolume("game"));
        Assert.Equal(40, routing.Rules.SendVolumes["game"]);                     // what the capture reads
        Assert.Equal((1, 1), (saves, changes));                                   // no save, plan or device check per tick
        routing.CommitSendVolume("game");                                         // the slider came to rest
        Assert.Equal((2, 1), (saves, changes));
        Assert.Equal(40, config.AppSendVolumes["game"]);
        routing.SetSendVolume("game", 50);
        Assert.Equal((3, 1), (saves, changes));
        routing.PreviewSendVolume("browser", 70);                                 // only an app whose own volume is on
        Assert.Null(routing.GetSendVolume("browser"));
        routing.CommitSendVolume("browser");
        Assert.Equal((3, 1), (saves, changes));
    }

    [Fact]
    public void Turning_the_own_volume_off_remembers_its_level_for_the_next_time()
    {
        var config = new AppConfig();
        var routing = new AppRouting(config, () => { });
        routing.SetSendVolume("game", 30);
        Assert.Null(routing.LastSendVolume("game"));
        routing.SetSendVolume("game", null);
        Assert.Null(routing.GetSendVolume("game"));
        Assert.Equal(30, routing.LastSendVolume("GAME.EXE"));
        Assert.Equal(30, JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config))!.AppSendVolumesOff["game"]);
        Assert.Empty(JsonSerializer.Deserialize<AppConfig>("{}")!.AppSendVolumesOff);
        routing.SetSendVolume("game", 25); // on again (MixerPage.SeedSendPercent picked the level)
        Assert.Null(routing.LastSendVolume("game"));
    }

    [Theory]
    [InlineData(null, 0.2f, 20)]          // exactly what the HomePod already plays: Windows 20 % x 1/20 % x 20 %
    [InlineData(null, 0.206f, 20)]        // rounded down, never above
    [InlineData(null, 1f, 100)]
    [InlineData(null, 0.005f, 0)]
    [InlineData(null, 0f, 0)]
    [InlineData(null, float.NaN, 0)]      // not known: nothing louder
    [InlineData(10, 0.2f, 10)]            // the level it had when turned off
    [InlineData(60, 0.2f, 20)]            // ... but never above the Windows level now
    [InlineData(150, 0.5f, 50)]
    [InlineData(-5, 0.5f, 0)]
    public void Turning_the_own_volume_on_never_makes_the_homepod_louder(int? remembered, float windows, int expected)
    {
        int seed = MixerPage.SeedSendPercent(remembered, windows);
        Assert.Equal(expected, seed);
        if (float.IsFinite(windows)) Assert.True(seed / 100f <= windows + 1e-6f);
    }

    [Fact]
    public void Statuses_of_apps_no_longer_sent_with_their_own_level_are_forgotten()
    {
        var routing = new AppRouting(new AppConfig(), () => { });
        routing.ReportSendStatus("game", SendVolumeStatus.DifferentSessionLevels);
        routing.ReportSendStatus("browser", SendVolumeStatus.MutedOrZero);
        routing.KeepSendStatus([new RouteTarget(10, "browser", false) { SendVolumePercent = 50 }, new RouteTarget(20, "game", false)]);
        Assert.Equal(SendVolumeStatus.Waiting, routing.SendStatus("game"));      // its own level was turned off
        Assert.Equal(SendVolumeStatus.MutedOrZero, routing.SendStatus("browser"));
        routing.KeepSendStatus([]);                                               // its sessions ended
        Assert.Equal(SendVolumeStatus.Waiting, routing.SendStatus("browser"));
        routing.ReportSendStatus("browser", SendVolumeStatus.BelowMinimum);
        routing.ClearSendStatus();                                                // the capture stopped
        Assert.Equal(SendVolumeStatus.Waiting, routing.SendStatus("browser"));
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
