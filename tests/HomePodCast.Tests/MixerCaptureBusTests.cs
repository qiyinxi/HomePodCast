using HomePodCast.Audio;

namespace HomePodCast.Tests;

public sealed class MixerCaptureBusTests
{
    [Fact]
    public void Browser_and_nested_roots_are_captured_once_and_follow_the_outer_owner()
    {
        var c = MixerRoutingV2Tests.Speaker();
        var routing = new MixerRoutingSettings { Apps = new()
            { ["browser"] = MixerRoute.Current, ["child"] = MixerRoute.Local } };
        var processes = new Dictionary<uint, ProcessEntry>
        { [1] = new(1, 0, "browser"), [2] = new(2, 1, "browser"), [3] = new(3, 2, "child") };
        var plan = MixerCapturePlanner.Plan(1, routing, c, [new(1, false), new(2, false), new(3, false), new(0, true)], processes);
        Assert.Single(plan.Roots);
        Assert.Equal(1u, plan.Roots[0].Target.RootPid);
        Assert.Equal("browser", plan.Roots[0].Target.Key);
    }

    [Fact]
    public void Distinct_markers_and_send_levels_reach_only_their_destination()
    {
        var inputs = new Dictionary<uint, FakeInput>();
        using var hub = new MixerCaptureHub(44100, t => inputs[t.RootPid] = new(t.RootPid * .1f),
            gain: (t, _) => new(1, t.SendVolumePercent.GetValueOrDefault(100) / 100f));
        var a = Fifo(); var b = Fifo();
        using var ca = hub.Subscribe("a", a); using var cb = hub.Subscribe("b", b); ca.Start(); cb.Start();
        hub.Apply(new(1, [Root(1, "a", 50), Root(2, "b")]));
        hub.Cycle();
        AssertConstant(Read(a, 200), .05f);
        AssertConstant(Read(b, 200), .2f);
        Assert.Equal(2, inputs.Count);
        Assert.Equal(1, inputs[1].Reads);
    }

    [Fact]
    public void Moving_one_root_removes_its_pending_contribution_without_erasing_another_app()
    {
        using var hub = new MixerCaptureHub(44100, t => new FakeInput(t.RootPid * .1f));
        var a = Fifo(); var b = Fifo();
        using var ca = hub.Subscribe("a", a); using var cb = hub.Subscribe("b", b); ca.Start(); cb.Start();
        hub.Apply(new(1, [Root(1, "a"), Root(2, "a")])); hub.Cycle();
        int queued = a.Depth;
        hub.Apply(new(2, [Root(1, "b"), Root(2, "a")]));
        Assert.Equal(queued, a.Depth);
        AssertConstant(Read(a, 200), .2f);
        hub.Cycle();
        AssertConstant(Read(b, 200), .1f);
        hub.Apply(new(1, [Root(1, "a"), Root(2, "a")]));
        Assert.Equal(2, hub.Generation);
    }

    [Fact]
    public void FIFO_recomputes_clipped_samples_when_a_contributor_moves()
    {
        var fifo = Fifo();
        fifo.WriteMixer(new() { [1] = Enumerable.Repeat(.8f, 800).ToArray(), [2] = Enumerable.Repeat(.7f, 800).ToArray() }, 400);
        AssertConstant(Read(fifo, 10), 1);
        fifo.RemoveMixerSource(1);
        AssertConstant(Read(fifo, 200), .7f);
    }

    [Fact]
    public void Mic_is_read_once_per_cycle_and_copied_to_each_bus()
    {
        var mic = new FakeMic(); var sources = new MixSources(); sources.Add(mic);
        long now = 0;
        using var hub = new MixerCaptureHub(44100, _ => new FakeInput(0), sources) { Clock = () => now };
        var a = Fifo(); var b = Fifo();
        using var ca = hub.Subscribe("a", a); using var cb = hub.Subscribe("b", b); ca.Start(); cb.Start();
        hub.Cycle();
        Assert.Equal(0, a.Depth);   // no app plays to either: an empty cycle alone writes nothing
        now += Ms(30);
        hub.Cycle();
        Assert.Equal(1, mic.Reads);
        Assert.Equal(1323, a.Depth); Assert.Equal(1323, b.Depth);   // the 30 ms since, on the wall clock
        AssertConstant(Read(a, 200), .25f); AssertConstant(Read(b, 200), .25f);
    }

    [Fact]
    public void Each_bus_gets_every_mic_frame_once_whatever_it_writes_per_cycle()
    {
        // 2026-10-27 review: the hub read a fixed 441 mic frames per cycle and cut or padded them to what each bus
        // wrote, so a bus writing two packets in one cycle got a 10 ms hole in the voice. Bus a also goes idle for
        // five cycles and carries the mic alone meanwhile; bus b's packets come at other times than a's.
        var mic = new RampMic(); var sources = new MixSources(); sources.Add(mic);
        int[] a = [441, 0, 882, 441, 441, 0, 882, 441, 0, 0, 0, 0, 0, 441, 441, 441, 0, 882, 441, 441];
        int[] b = [882, 0, 441, 441, 882, 0, 441, 441, 441, 441, 0, 882, 441, 441, 0, 882, 441, 441, 441, 441];
        int cycle = 0;
        long now = 0;
        using var hub = new MixerCaptureHub(44100, t => new PacketInput(t.RootPid == 1 ? a : b, () => cycle), sources,
            steerClock: false) { Clock = () => now };
        var fa = Fifo(); var fb = Fifo();
        using var ca = hub.Subscribe("a", fa); using var cb = hub.Subscribe("b", fb); ca.Start(); cb.Start();
        hub.Apply(new(1, [Root(1, "a"), Root(2, "b")]));
        var heardA = new List<float>(); var heardB = new List<float>();
        for (cycle = 0; cycle < 400; cycle++)
        {
            now += Ms(10);
            hub.Cycle();
            Drain(fa, heardA); Drain(fb, heardB);
        }
        AssertRamp(heardA); AssertRamp(heardB);
        Assert.InRange(heardA.Count / 2, 400 * 441 - 63 - 1323, 400 * 441);
        Assert.InRange(heardB.Count / 2, 400 * 441 - 63 - 1323, 400 * 441);
        Assert.InRange(mic.Frames, 400 * 441 - 63, 400 * 441 + 1323);   // real time, as far as the busier bus
    }

    [Fact]
    public void A_destination_that_drifts_behind_skips_single_mic_frames_and_stays_close()
    {
        // Each destination's resampler follows its own receiver: one that runs slower writes fewer frames, and would
        // fall further and further behind the mic the other one keeps fresh.
        var mic = new RampMic(); var sources = new MixSources(); sources.Add(mic);
        int cycle = 0;
        long now = 0;
        using var hub = new MixerCaptureHub(44100, t => new PacketInput(t.RootPid == 1 ? [441] : [440], () => cycle), sources,
            steerClock: false) { Clock = () => now };
        var fa = Fifo(); var fb = Fifo();
        using var ca = hub.Subscribe("a", fa); using var cb = hub.Subscribe("b", fb); ca.Start(); cb.Start();
        hub.Apply(new(1, [Root(1, "a"), Root(2, "b")]));
        var heardA = new List<float>(); var heardB = new List<float>();
        for (cycle = 0; cycle < 3000; cycle++)
        {
            now += Ms(10);
            hub.Cycle();
            Drain(fa, heardA); Drain(fb, heardB);
        }
        AssertRamp(heardA);
        int skipped = 0;
        for (int i = 2; i < heardB.Count; i += 2)
        {
            int step = RampMic.Distance(heardB[i - 2], heardB[i]);
            Assert.InRange(step, 1, 2);
            skipped += step - 1;
        }
        int behind = RampMic.Distance(heardB[^2], heardA[^2]);
        Assert.InRange(behind, 1300, 1324);   // 30 ms at most: the rest went one frame at a time
        Assert.InRange(skipped, 3000 - 1323 - 64, 3000 - 1323);
    }

    [Fact]
    public void A_live_input_a_cycle_or_two_late_gets_no_silence_in_front_of_its_audio()
    {
        var sources = new MixSources(); sources.Add(new FakeMic { Value = 0 });   // the mic is on, quiet
        int[] packets = [441, 441, 0, 882, 441, 0, 0, 1323, 441, 441];
        int cycle = 0;
        long now = 0;
        using var hub = new MixerCaptureHub(44100, _ => new PacketInput(packets, () => cycle, .1f), sources,
            steerClock: false) { Clock = () => now };
        var fifo = Fifo(); using var bus = hub.Subscribe("a", fifo); bus.Start();
        hub.Apply(new(1, [Root(1, "a")]));
        var heard = new List<float>();
        for (cycle = 0; cycle < 200; cycle++)
        {
            now += Ms(10);
            hub.Cycle();
            Drain(fifo, heard);
        }
        AssertConstant([.. heard], .1f);                 // the app's audio only, nothing between its packets
        Assert.Equal(200 * 441 - 63, heard.Count / 2);   // every frame once (the resampler holds 63 back)
    }

    [Fact]
    public void An_idle_destination_carries_the_mic_in_real_time_and_nothing_while_it_is_off()
    {
        var mic = new FakeMic(); var sources = new MixSources(); sources.Add(mic);
        long now = 0;
        using var hub = new MixerCaptureHub(44100, _ => new FakeInput(0), sources) { Clock = () => now };
        var fifo = Fifo(); using var bus = hub.Subscribe("a", fifo); bus.Start();
        var heard = new List<float>();
        // The capture thread's cycles run 10-20 ms apart: at 441 frames each the speaker ran dry.
        for (int i = 0; i < 400; i++)
        {
            now += Ms(i % 2 == 0 ? 10 : 15);
            hub.Cycle();
            Drain(fifo, heard);
        }
        Assert.InRange(heard.Count / 2, 220500 - 2, 220500 + 2);   // 5 s
        AssertConstant([.. heard], .25f);

        mic.On = false;   // a closed mic: an idle destination writes no silence of its own
        for (int i = 0; i < 100; i++) { now += Ms(12); hub.Cycle(); }
        Assert.Equal(0, fifo.Depth);
        mic.On = true;
        now += Ms(10); hub.Cycle(); now += Ms(10); hub.Cycle();
        Assert.Equal(0, fifo.Depth);                     // back after a 20 ms gap
        for (int i = 0; i < 6; i++) { now += Ms(10); hub.Cycle(); }
        Assert.InRange(fifo.Depth, 3528 - 1, 3528 + 1);  // then all 80 ms since it came back
    }

    [Fact]
    public void Slow_destination_cannot_grow_queues_or_change_the_game_samples()
    {
        using var hub = new MixerCaptureHub(44100, t => new FakeInput(t.RootPid * .1f), steerClock: false);
        var game = Fifo(); var slow = Fifo();
        using var cg = hub.Subscribe("game", game); using var cs = hub.Subscribe("slow", slow); cg.Start(); cs.Start();
        hub.Apply(new(1, [Root(1, "game"), Root(2, "slow")]));
        for (int i = 0; i < 1000; i++)
        {
            hub.Cycle();
            AssertConstant(Read(game, i == 0 ? 378 : 441), .1f);
            Assert.InRange(slow.Depth, 0, slow.CapFrames);
        }
        Assert.True(slow.DroppedFrames > 0);
    }

    [Fact]
    public void Retarget_keeps_the_input_alive_while_the_replacement_bus_is_connecting()
    {
        var input = new FakeInput(.1f); int opens = 0;
        using var hub = new MixerCaptureHub(44100, _ => { opens++; return input; });
        var old = hub.Subscribe("old", Fifo()); old.Start();
        hub.Apply(new(1, [Root(1, "old")])); hub.Cycle();
        hub.Retarget(new Dictionary<string, string?> { ["app1"] = "next" });
        old.Dispose(); hub.Cycle();
        Assert.Equal(1, hub.InputCount); Assert.Equal(0, input.Disposals); Assert.False(hub.IsCapturing(1));
        var fifo = Fifo(); using var next = hub.Subscribe("next", fifo); next.Start();
        hub.Apply(new(2, [Root(1, "next")])); hub.Cycle();
        Assert.True(hub.IsCapturing(1)); Assert.Equal(1, opens); AssertConstant(Read(fifo, 200), .1f);
    }

    [Fact]
    public void Clock_steering_of_a_backlogged_destination_does_not_steer_its_neighbor()
    {
        using var hub = new MixerCaptureHub(44100, _ => new FakeInput(.1f));
        using var reference = new MixerCaptureHub(44100, _ => new FakeInput(.1f));
        var a = Fifo(); var b = Fifo();
        var referenceFifo = Fifo(); using var referenceBus = reference.Subscribe("game", referenceFifo); referenceBus.Start();
        reference.Apply(new(1, [Root(1, "game")]));
        using var game = hub.Subscribe("game", a); using var slow = hub.Subscribe("slow", b); game.Start(); slow.Start();
        hub.Apply(new(1, [Root(1, "game"), Root(2, "slow")]));
        var scratch = new float[882];
        for (int cycle = 0; cycle < 1000; cycle++)
        {
            hub.Cycle();
            reference.Cycle(); referenceFifo.Read(scratch);
            a.Read(scratch);
        }
        Assert.InRange(slow.DriftPpm, 400, 500);
        Assert.Equal(referenceBus.DriftPpm, game.DriftPpm); Assert.Equal(referenceFifo.Depth, a.Depth);
        Assert.Equal(44, a.TargetFrames); Assert.Equal(44, b.TargetFrames);
        Assert.InRange(a.Depth, 0, a.CapFrames); Assert.True(b.DroppedFrames > 0);
    }

    [Fact]
    public void PID_reuse_reopens_capture_and_disposal_of_one_bus_keeps_the_other_alive()
    {
        var opened = new List<FakeInput>();
        using var hub = new MixerCaptureHub(44100, _ => { var i = new FakeInput(.1f); opened.Add(i); return i; });
        using var a = hub.Subscribe("a", Fifo()); using var b = hub.Subscribe("b", Fifo()); a.Start(); b.Start();
        hub.Apply(new(1, [Root(1, "a"), Root(2, "b")]));
        hub.Apply(new(2, [Root(1, "a") with { ProcessIdentity = 5 }, Root(2, "b")]));
        Assert.Equal(3, opened.Count); Assert.Equal(1, opened[0].Disposals);
        a.Dispose();
        Assert.Equal(1, hub.InputCount); Assert.Equal(0, opened[1].Disposals);
        hub.Dispose();
        Assert.All(opened, i => Assert.Equal(1, i.Disposals));
    }

    [Fact]
    public void Capture_failure_and_Local_rules_do_not_leave_a_capture_or_a_second_local_copy()
    {
        var c = MixerRoutingV2Tests.Speaker();
        var routing = new MixerRoutingSettings { Default = MixerRoute.Local };
        Assert.Empty(MixerCapturePlanner.Plan(1, routing, c, [new(1, false)],
            new Dictionary<uint, ProcessEntry> { [1] = new(1, 0, "local") }).Roots);
        var broken = new FakeInput(.1f) { Broken = true };
        using var hub = new MixerCaptureHub(44100, _ => broken);
        using var bus = hub.Subscribe("a", Fifo()); bus.Start();
        hub.Apply(new(1, [Root(1, "a")])); hub.Cycle();
        Assert.False(hub.IsCapturing(1)); Assert.Equal(1, broken.Disposals);
    }

    [Fact]
    public void Both_keeps_the_same_remote_root_without_local_attenuation()
    {
        var c = MixerRoutingV2Tests.Speaker();
        var routing = new MixerRoutingSettings { Default = MixerRoute.Current with { AlsoLocal = true } };
        var plan = MixerCapturePlanner.Plan(1, routing, c, [new(1, false)],
            new Dictionary<uint, ProcessEntry> { [1] = new(1, 0, "both") });
        Assert.Single(plan.Roots); Assert.False(plan.Roots[0].Target.Compensate);
        Assert.Equal(MixerDestinationResolver.Resolve(c, MixerRoute.Current).Key, plan.Roots[0].Destination);
    }

    [Fact]
    public void Warm_capture_cycles_allocate_no_packet_objects_and_repeated_stop_releases_every_root()
    {
        for (int turn = 0; turn < 4; turn++)
        {
            var inputs = new List<FakeInput>();
            using var hub = new MixerCaptureHub(44100, _ => { var input = new FakeInput(.1f); inputs.Add(input); return input; });
            var fifo = Fifo(); using var capture = hub.Subscribe("a", fifo); capture.Start();
            hub.Apply(new(1, [Root(1, "a")]));
            var samples = new float[882];
            for (int i = 0; i < 100; i++) { hub.Cycle(); fifo.Read(samples); }
            // A gen0 GC first, as Signals.Allocated does: otherwise a background GC can drop this thread's allocation
            // context and the count jumps by up to ~8 KB that nothing allocated (2026-10-28: 6008 bytes in the
            // v1.3.0-beta.5 tag build, while the same commit passed four other runs).
            GC.Collect(0);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) { hub.Cycle(); fifo.Read(samples); }
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocated);
            capture.Dispose();
            Assert.Equal(0, hub.BusCount); Assert.Equal(0, hub.InputCount);
            Assert.All(inputs, input => Assert.Equal(1, input.Disposals));
        }
    }

    internal static AudioFifo Fifo() => new(44100, 1, 100);
    internal static MixerRoot Root(uint pid, string destination, int? volume = null) =>
        new(new(pid, "app" + pid, false) { SendVolumePercent = volume }, destination);
    internal static float[] Read(AudioFifo fifo, int frames)
    { var data = new float[frames * 2]; Assert.True(fifo.Read(data)); return data; }
    private static void AssertConstant(float[] values, float expected) => Assert.All(values, v => Assert.InRange(v, expected - .0001f, expected + .0001f));
    private static long Ms(double ms) => (long)(ms * System.Diagnostics.Stopwatch.Frequency / 1000);
    /// <summary>Everything the bus wrote since the last call (as a sender would hear it).</summary>
    private static void Drain(AudioFifo fifo, List<float> heard)
    {
        int frames = fifo.Depth;
        if (frames == 0) return;
        var data = new float[frames * 2];
        Assert.True(fifo.Read(data));
        heard.AddRange(data);
    }
    private static void AssertRamp(List<float> heard)
    {
        Assert.Equal(0, heard[0]);
        for (int i = 2; i < heard.Count; i += 2)
            if (RampMic.Distance(heard[i - 2], heard[i]) != 1 || heard[i + 1] != heard[i])
                Assert.Fail($"mic frame {i / 2}: {RampMic.Number(heard[i - 2])} then {RampMic.Number(heard[i])}");
    }
    internal sealed class FakeInput(float marker) : IMixerProcessInput
    {
        private readonly float[] _samples = Enumerable.Repeat(marker, 882).ToArray();
        public int Reads, Disposals;
        public bool Broken;
        public bool Drain(List<float> samples) { Reads++; samples.AddRange(_samples); return !Broken; }
        public void Dispose() => Disposals++;
    }
    private sealed class FakeMic : IMixSource
    {
        public int Reads;
        public float Value = .25f;
        public bool On = true;
        public bool HasAudio => On;
        public void Read(Span<float> dest) { Reads++; dest.Fill(Value); }
    }
    /// <summary>Numbers its frames (0.5 s around), so a gap or a repeat shows.</summary>
    private sealed class RampMic : IMixSource
    {
        public const float Step = 1e-5f;
        public const int Modulo = 50_000;
        public long Frames;
        public void Read(Span<float> dest)
        { for (int i = 0; i < dest.Length; i += 2) dest[i] = dest[i + 1] = Frames++ % Modulo * Step; }
        public static int Number(float sample) => (int)Math.Round(sample / Step);
        public static int Distance(float from, float to) => (Number(to) - Number(from) + Modulo) % Modulo;
    }
    /// <summary>An app whose packets arrive unevenly: so many frames in each cycle, in turn.</summary>
    private sealed class PacketInput(int[] frames, Func<int> cycle, float marker = 0) : IMixerProcessInput
    {
        public bool Drain(List<float> samples)
        {
            for (int i = frames[cycle() % frames.Length] * 2; i > 0; i--) samples.Add(marker);
            return true;
        }
        public void Dispose() { }
    }
}
