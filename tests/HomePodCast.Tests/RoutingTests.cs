using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using HomePodCast.Audio;

namespace HomePodCast.Tests;

public class RoutePlannerTests
{
    private static RouteRules Rules(AudioRoute def, params (string Key, AudioRoute Route)[] apps) =>
        new(def, apps.ToDictionary(a => a.Key, a => a.Route));

    // explorer(1) -> game(100), discord(200), chrome(10) -> chrome(11 audio), chrome(12 renderer)
    private static readonly Dictionary<uint, ProcessEntry> Procs = new[]
    {
        new ProcessEntry(1, 0, "explorer"),
        new ProcessEntry(100, 1, "game"),
        new ProcessEntry(200, 1, "discord"),
        new ProcessEntry(10, 1, "chrome"),
        new ProcessEntry(11, 10, "chrome"),
        new ProcessEntry(12, 10, "chrome"),
        new ProcessEntry(20, 1, "launcher"),
        new ProcessEntry(21, 20, "shooter"),
    }.ToDictionary(p => p.Pid);

    private static SessionEntry S(uint pid) => new(pid, false);

    [Fact]
    public void Without_rules_the_whole_output_is_captured_as_before()
    {
        var plan = RoutePlanner.Plan(RouteRules.Today, [S(100), S(200)], Procs);
        Assert.True(plan.Endpoint);
        Assert.Empty(plan.Targets);
        Assert.Empty(plan.Silenced);
    }

    [Fact]
    public void A_rule_that_matches_the_default_keeps_the_fast_path()
    {
        Assert.True(RoutePlanner.Plan(Rules(AudioRoute.HomePod, ("game", AudioRoute.HomePod)), [S(100)], Procs).Endpoint);
        Assert.True(RoutePlanner.Plan(Rules(AudioRoute.Both, ("game", AudioRoute.Both)), [S(100)], Procs).Endpoint);
    }

    [Fact]
    public void Local_app_is_neither_captured_nor_silenced_and_the_rest_goes_to_the_speaker_only()
    {
        var plan = RoutePlanner.Plan(Rules(AudioRoute.HomePod, ("discord", AudioRoute.Local)),
            [S(100), S(200), new SessionEntry(0, true)], Procs);
        Assert.False(plan.Endpoint);
        Assert.Equal([new RouteTarget(100, "game", Compensate: true)], plan.Targets);
        Assert.Equal([new Silence(100, 100)], plan.Silenced);
    }

    [Fact]
    public void Both_is_captured_without_silencing()
    {
        var plan = RoutePlanner.Plan(Rules(AudioRoute.HomePod, ("discord", AudioRoute.Both)), [S(100), S(200)], Procs);
        Assert.Equal([new RouteTarget(100, "game", true), new RouteTarget(200, "discord", false)], plan.Targets);
        Assert.Equal([new Silence(100, 100)], plan.Silenced);
    }

    [Fact]
    public void Default_local_sends_only_apps_with_their_own_rule()
    {
        var rules = Rules(AudioRoute.Local, ("game", AudioRoute.HomePod));
        Assert.True(Rules(AudioRoute.Local).NeedsRouting);
        var plan = RoutePlanner.Plan(rules, [S(100), S(200), S(11)], Procs);
        Assert.Equal([new RouteTarget(100, "game", true)], plan.Targets);
        Assert.Equal([new Silence(100, 100)], plan.Silenced);
    }

    [Fact]
    public void Multi_process_browser_is_captured_once_from_its_main_process()
    {
        var plan = RoutePlanner.Plan(Rules(AudioRoute.HomePod, ("discord", AudioRoute.Local)), [S(11), S(12)], Procs);
        Assert.Equal([new RouteTarget(10, "chrome", true)], plan.Targets);
        Assert.Equal([new Silence(11, 10), new Silence(12, 10)], plan.Silenced);
    }

    [Fact]
    public void A_child_inside_a_captured_app_follows_that_app()
    {
        // The launcher's include-tree capture contains the child anyway, so the child cannot be "local".
        var plan = RoutePlanner.Plan(Rules(AudioRoute.HomePod, ("shooter", AudioRoute.Local)), [S(20), S(21)], Procs);
        Assert.Equal([new RouteTarget(20, "launcher", true)], plan.Targets);
        Assert.Equal([new Silence(20, 20), new Silence(21, 20)], plan.Silenced);

        // Launcher "both": its tree is captured at normal level, so the child must not be silenced.
        plan = RoutePlanner.Plan(Rules(AudioRoute.HomePod, ("launcher", AudioRoute.Both)), [S(20), S(21)], Procs);
        Assert.Equal([new RouteTarget(20, "launcher", false)], plan.Targets);
        Assert.Empty(plan.Silenced);
    }

    [Fact]
    public void A_child_of_a_local_app_gets_its_own_capture()
    {
        var plan = RoutePlanner.Plan(Rules(AudioRoute.HomePod, ("launcher", AudioRoute.Local)), [S(20), S(21)], Procs);
        Assert.Equal([new RouteTarget(21, "shooter", true)], plan.Targets);
        Assert.Equal([new Silence(21, 21)], plan.Silenced);
    }

    [Fact]
    public void Exited_processes_and_parent_cycles_are_ignored()
    {
        var procs = new Dictionary<uint, ProcessEntry>
        {
            [5] = new(5, 6, "a"),
            [6] = new(6, 5, "a"),   // pid reuse can make a loop
        };
        var plan = RoutePlanner.Plan(Rules(AudioRoute.HomePod, ("x", AudioRoute.Local)), [S(5), S(999)], procs);
        Assert.Single(plan.Targets);
    }

    [Theory]
    [InlineData("Discord.exe", "discord")]
    [InlineData("chrome", "chrome")]
    [InlineData(" Game.EXE ", "game")]
    public void Rule_keys_are_lower_case_executable_names(string name, string key) =>
        Assert.Equal(key, RouteRules.KeyFor(name));
}

public class SessionAttenuationTests
{
    [Fact]
    public void Attenuation_round_trips_and_marks_the_session()
    {
        foreach (float v in new[] { 1f, 0.5f, 0.01f })
        {
            float raw = SessionAttenuation.Attenuated(v);
            Assert.True(SessionAttenuation.IsAttenuated(raw));
            Assert.Equal(v, SessionAttenuation.ToLogical(raw), 5);
            Assert.Equal(raw, SessionAttenuation.Attenuated(raw)); // idempotent
        }
        Assert.False(SessionAttenuation.IsAttenuated(0f));       // user volume 0 is left alone
        Assert.False(SessionAttenuation.IsAttenuated(0.01f));    // 1 %: the lowest a slider sets
        Assert.Equal(0.3f, SessionAttenuation.ToLogical(0.3f));
    }

    [Fact]
    public void Compensation_only_for_confirmed_quiet_audio()
    {
        float quiet = 0.8f * SessionAttenuation.Epsilon;   // an attenuated app at -2 dBFS
        Assert.Equal(SessionAttenuation.Gain, SessionAttenuation.CompensationGain(true, true, quiet));
        Assert.Equal(1f, SessionAttenuation.CompensationGain(false, true, quiet));  // "both": as captured
        Assert.Equal(1f, SessionAttenuation.CompensationGain(true, false, quiet));  // not silenced long enough
        Assert.Equal(1f, SessionAttenuation.CompensationGain(true, true, 0.01f));   // unattenuated: never x100000
    }

    // Gate timing in milliseconds: confirm 100, verification must be at most 100 old.
    private static CompensationGate Gate() => new(confirmDelay: 100, verifyWindow: 100);

    [Fact]
    public void Gate_opens_only_after_the_confirm_delay_and_while_volumes_are_being_verified()
    {
        var gate = Gate();
        gate.Silenced(7, now: 0);
        gate.Verified(7, now: 10);
        Assert.False(gate.IsOpen(7, 50));     // attenuated audio has not reached the capture yet
        gate.Verified(7, now: 100);
        Assert.True(gate.IsOpen(7, 120));
        Assert.False(gate.IsOpen(8, 120));    // another target
        // The router stopped re-reading the volumes: an unverified session could be audible again.
        Assert.False(gate.IsOpen(7, 201));
        gate.Verified(7, now: 205);
        Assert.True(gate.IsOpen(7, 210));
    }

    [Fact]
    public void Volume_raised_elsewhere_closes_the_gate_at_once_for_the_confirm_delay()
    {
        var gate = Gate();
        gate.Silenced(7, 0);
        gate.Verified(7, 100);
        Assert.True(gate.IsOpen(7, 110));
        // Windows mixer drags the app up: the router silences it again (and does not verify it this round).
        gate.Silenced(7, 115);
        Assert.False(gate.IsOpen(7, 116));
        gate.Verified(7, 130);
        Assert.False(gate.IsOpen(7, 200));    // the louder audio may still be on its way to the capture
        gate.Verified(7, 210);
        Assert.True(gate.IsOpen(7, 215));
    }

    [Fact]
    public void Keep_does_not_postpone_an_open_gate_and_a_device_switch_closes_every_gate()
    {
        var gate = Gate();
        gate.Silenced(7, 0);
        gate.Silenced(9, 0);
        gate.Verified(7, 100);
        gate.Verified(9, 100);
        gate.Keep(7, 105);                    // unchanged at the next refresh
        Assert.True(gate.IsOpen(7, 110));
        gate.CloseAll();                      // default output changed: sessions there are not silenced yet
        Assert.False(gate.IsOpen(7, 111));
        Assert.False(gate.IsOpen(9, 111));
        gate.Keep(7, 120);
        gate.Verified(7, 125);
        Assert.False(gate.IsOpen(7, 200));
        Assert.True(gate.IsOpen(7, 220));
        gate.Close(7);
        Assert.False(gate.IsOpen(7, 230));
        Assert.Empty(gate.Roots);
    }

    [Fact]
    public void A_later_silencing_never_shortens_a_closed_gate()
    {
        var gate = Gate();
        gate.Silenced(7, 50);     // the session's volume event, at once
        gate.Silenced(7, 40);     // the router's check, which read its clock a moment earlier
        gate.Verified(7, 145);
        Assert.False(gate.IsOpen(7, 149));
        Assert.True(gate.IsOpen(7, 150));
    }

    private static long Ms(double ms) => (long)(ms * Stopwatch.Frequency / 1000);

    /// <summary>Call the handler through its COM vtable, as the audio service does (slot 3 + 2 = OnSimpleVolumeChanged).</summary>
    private static int Report(IntPtr events, float volume, Guid context)
    {
        IntPtr ctx = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.StructureToPtr(context, ctx, false);
            IntPtr fn = Marshal.ReadIntPtr(Marshal.ReadIntPtr(events), 5 * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer<SimpleVolumeChanged>(fn)(events, volume, 0, ctx);
        }
        finally
        {
            Marshal.FreeHGlobal(ctx);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SimpleVolumeChanged(IntPtr self, float volume, int mute, IntPtr context);

    [Fact]
    public void A_volume_raised_elsewhere_stops_compensation_the_moment_the_session_reports_it()
    {
        using var router = new SessionRouter(new AppRouting(new AppConfig(), () => { }), _ => true);
        var handler = new SessionRouter.VolumeEvents(router, () => 7);
        IntPtr events = Marshal.GetComInterfaceForObject<SessionRouter.VolumeEvents, IAudioSessionEvents>(handler);
        try
        {
            var iid = typeof(IAudioSessionEvents).GUID;
            Assert.Equal(0, Marshal.QueryInterface(events, in iid, out var same));
            Marshal.Release(same);

            // Silenced long ago and just read back attenuated: compensated.
            router.Gate.Silenced(7, Stopwatch.GetTimestamp() - Ms(500));
            bool Open()
            {
                long now = Stopwatch.GetTimestamp();
                router.Gate.Verified(7, now);
                return router.IsConfirmed(7, now);
            }
            Assert.True(Open());

            // Still inaudible here, or our own change: nothing to stop.
            Assert.Equal(0, Report(events, SessionAttenuation.Attenuated(0.8f), Guid.NewGuid()));
            Assert.Equal(0, Report(events, 0f, Guid.NewGuid()));
            Assert.Equal(0, Report(events, 0.6f, SessionRouter.OwnContext));
            Assert.True(Open());

            // The Windows mixer raises the app to 60 %: closed now, not at the router's next check, even though the
            // router still reads the volumes back; and for the whole confirm delay, while that audio is on its way.
            long before = Stopwatch.GetTimestamp();
            Assert.Equal(0, Report(events, 0.6f, Guid.NewGuid()));
            long after = Stopwatch.GetTimestamp();
            Assert.False(Open());
            router.Gate.Verified(7, before + SessionRouter.ConfirmDelay - 1);
            Assert.False(router.IsConfirmed(7, before + SessionRouter.ConfirmDelay - 1));
            router.Gate.Verified(7, after + SessionRouter.ConfirmDelay);
            Assert.True(router.IsConfirmed(7, after + SessionRouter.ConfirmDelay));
        }
        finally
        {
            Marshal.Release(events);
        }
    }

    [Fact]
    public void Router_rechecks_volumes_well_inside_the_capture_delay()
    {
        // A volume raised elsewhere must be caught before its louder audio reaches the process-loopback
        // capture (~35 ms later). Waits round up to the 15.6 ms timer tick.
        Assert.True(Math.Max(SessionRouter.WatchIntervalMs, 15.625) < RoutedCapture.RoutedExtraLatencyMs);
        Assert.True(SessionRouter.VerifyWindow <= System.Diagnostics.Stopwatch.Frequency / 10);
    }
}

public class StreamMixerTests
{
    private static float[] Frames(int n, float value) => Enumerable.Repeat(value, n * 2).ToArray();

    [Fact]
    public void Lockstep_inputs_are_summed_with_their_gains()
    {
        var m = new StreamMixer(1000);
        m.Add(1);
        m.Add(2);
        m.Push(1, Frames(441, 0.1f));
        m.Push(2, Frames(441, 0.2f), gain: 2f);
        var output = new List<float>();
        Assert.Equal(441, m.Mix(output));
        Assert.All(output, s => Assert.Equal(0.5f, s, 5));
    }

    [Fact]
    public void A_packet_that_lands_a_cycle_late_stays_aligned()
    {
        var m = new StreamMixer(1000);
        m.Add(1);
        m.Add(2);
        m.Push(1, Frames(441, 0.1f));
        var output = new List<float>();
        Assert.Equal(0, m.Mix(output));          // input 2 has not delivered yet: wait
        m.Push(1, Frames(441, 0.1f));
        m.Push(2, Frames(882, 0.2f));             // its two packets arrive together
        Assert.Equal(882, m.Mix(output));
        Assert.All(output, s => Assert.Equal(0.3f, s, 5));
    }

    [Fact]
    public void A_stalled_input_stops_holding_the_others_back()
    {
        var m = new StreamMixer(10_000) { StallCycles = 3 };
        m.Add(1);
        m.Add(2);
        var output = new List<float>();
        m.Push(1, Frames(100, 0.1f));
        m.Push(2, Frames(100, 0.1f));
        Assert.Equal(100, m.Mix(output));
        int mixed = 0;
        for (int cycle = 0; cycle < 4; cycle++)
        {
            m.Push(1, Frames(100, 0.1f));         // input 2 went quiet
            mixed += m.Mix(output);
        }
        Assert.Equal(400, mixed);                  // released once input 2 counts as stalled, nothing lost
        Assert.Equal(0, m.Pending(1));
    }

    [Fact]
    public void Backlog_is_trimmed_and_output_clamped()
    {
        var m = new StreamMixer(maxLagFrames: 50);
        m.Add(1);
        m.Add(2);
        m.Push(1, Frames(10, 0.9f));
        m.Push(2, Frames(300, 0.9f));
        var output = new List<float>();
        Assert.Equal(10, m.Mix(output));
        Assert.All(output, s => Assert.Equal(1f, s));   // 0.9 + 0.9 clamped
        Assert.Equal(50, m.Pending(2));                // 290 left, trimmed to the lag limit
    }

    private sealed class Constant(float value) : IMixSource
    {
        public int Frames;
        public void Read(Span<float> dest)
        {
            Frames += dest.Length / 2;
            dest.Fill(value);
        }
    }

    private sealed class Broken : IMixSource
    {
        public void Read(Span<float> dest) => throw new InvalidOperationException("unplugged");
    }

    [Fact]
    public void Extra_sources_are_pulled_frame_for_frame_and_mixed()
    {
        var mic = new Constant(0.25f);
        var mix = Frames(352, 0.5f);
        var scratch = Array.Empty<float>();
        StreamMixer.AddSources(mix, [mic, new Broken(), new Constant(0.5f)], ref scratch);
        Assert.Equal(352, mic.Frames);
        Assert.All(mix, s => Assert.Equal(1f, s));      // 0.5 + 0.25 + 0.5 clamped; the broken one skipped
    }

    [Fact]
    public void Mix_sources_list_is_copy_on_write()
    {
        var list = new MixSources();
        var a = new Constant(0);
        list.Add(a);
        var snapshot = list.Snapshot;
        list.Remove(a);
        Assert.Single(snapshot);
        Assert.Empty(list.Snapshot);
    }
}

public class AppRoutingTests
{
    [Fact]
    public void Rules_persist_in_config_and_switch_routing_on_and_off()
    {
        var config = new AppConfig();
        int saves = 0, changes = 0;
        var routing = new AppRouting(config, () => saves++);
        routing.Changed += () => changes++;
        Assert.False(routing.Rules.NeedsRouting);

        routing.Set("Discord.exe", AudioRoute.Local);
        Assert.Equal(AudioRoute.Local, routing.Get("discord"));
        Assert.Equal(AudioRoute.Local, config.AppRoutes["discord"]);
        Assert.True(routing.Rules.NeedsRouting);
        Assert.Equal(AudioRoute.HomePod, routing.Rules.For("game"));

        routing.Set("discord", AudioRoute.Local);  // no change: no save
        routing.Set("discord", null);              // back to the default
        Assert.Null(routing.Get("discord"));
        Assert.False(routing.Rules.NeedsRouting);
        Assert.Equal(2, saves);
        Assert.Equal(2, changes);

        routing.Default = AudioRoute.Both;
        Assert.Equal(AudioRoute.Both, config.RouteDefault);
        Assert.False(routing.Rules.NeedsRouting);   // everything "both" = the whole output
    }

    [Fact]
    public void Config_stores_routes_as_names()
    {
        var config = new AppConfig { RouteDefault = AudioRoute.Both };
        config.AppRoutes["game"] = AudioRoute.HomePod;
        config.AppRoutes["discord"] = AudioRoute.Local;
        var json = JsonSerializer.Serialize(config);
        Assert.Contains("\"RouteDefault\":\"Both\"", json);
        Assert.Contains("\"discord\":\"Local\"", json);
        var back = JsonSerializer.Deserialize<AppConfig>(json)!;
        Assert.Equal(AudioRoute.Both, back.RouteDefault);
        Assert.Equal(AudioRoute.Local, back.AppRoutes["discord"]);

        var old = JsonSerializer.Deserialize<AppConfig>("{\"LatencyMs\":120}")!;  // config from before routing
        Assert.Equal(AudioRoute.HomePod, old.RouteDefault);
        Assert.Empty(old.AppRoutes);
    }
}
