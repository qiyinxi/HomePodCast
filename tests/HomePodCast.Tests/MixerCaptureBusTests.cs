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
        using var hub = new MixerCaptureHub(44100, _ => new FakeInput(0), sources);
        var a = Fifo(); var b = Fifo();
        using var ca = hub.Subscribe("a", a); using var cb = hub.Subscribe("b", b); ca.Start(); cb.Start();
        hub.Cycle();
        Assert.Equal(1, mic.Reads);
        AssertConstant(Read(a, 200), .25f); AssertConstant(Read(b, 200), .25f);
    }

    [Fact]
    public void Slow_destination_cannot_grow_queues_or_change_the_game_samples()
    {
        using var hub = new MixerCaptureHub(44100, t => new FakeInput(t.RootPid * .1f));
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
    internal sealed class FakeInput(float marker) : IMixerProcessInput
    {
        private readonly float[] _samples = Enumerable.Repeat(marker, 882).ToArray();
        public int Reads, Disposals;
        public bool Broken;
        public bool Drain(List<float> samples) { Reads++; samples.AddRange(_samples); return !Broken; }
        public void Dispose() => Disposals++;
    }
    private sealed class FakeMic : IMixSource
    { public int Reads; public void Read(Span<float> dest) { Reads++; dest.Fill(.25f); } }
}
