using System.Diagnostics;
using HomePodCast.Audio;
using HomePodCast.UI;
using HomePodCast.UI.Pages;

namespace HomePodCast.Tests;

/// <summary>
/// 音乐分轨 without the module: the timelines, the mix of each destination (<see cref="StemBus"/>), and the whole mixer
/// with an in-process engine (<see cref="FakeStemEngine"/>). Clicks and zipper noise are checked on synthetic signals by
/// the largest step between two output samples, never by listening.
/// </summary>
public class StemTests
{
    [Fact]
    public void Missing_and_overwritten_results_never_alias_another_frame()
    {
        var timeline = new StemTimeline(8);
        Assert.False(timeline.Read(0, out _, out _));
        timeline.Write(0, [0.2f, 0.3f]);
        Assert.True(timeline.Read(0, out float left, out float right));
        Assert.Equal(0.2f, left); Assert.Equal(0.3f, right);
        timeline.Write(8, [0.8f, 0.9f]);
        Assert.False(timeline.Read(0, out _, out _));
        Assert.False(timeline.Has(0)); Assert.True(timeline.Has(8));
        Assert.True(timeline.Read(8, out left, out _)); Assert.Equal(0.8f, left);
    }

    [Fact]
    public void Invalid_preroll_is_discarded_and_channels_stay_in_order()
    {
        var timeline = new StemTimeline();
        timeline.Write(-1, [9f, 9f, 0.1f, -0.2f]);
        Assert.False(timeline.Read(-1, out _, out _));
        Assert.True(timeline.Read(0, out float l, out float r));
        Assert.Equal(0.1f, l); Assert.Equal(-0.2f, r);
    }

    [Theory]
    [InlineData(0.3f, 0.1f)]
    [InlineData(-0.5f, 0.25f)]
    public void Complementary_stems_reconstruct_mixture_and_equal_gains_need_no_estimate(float mixture, float vocal)
    {
        float v = StemMix.Sample(mixture, vocal, 1, 0), a = StemMix.Sample(mixture, vocal, 0, 1);
        Assert.Equal(mixture, v + a, 5);
        Assert.Equal(mixture, StemMix.Sample(mixture, float.NaN, 1, 1));
    }

    // ---- StemBus: one destination ----

    private static (StemTimeline Original, StemTimeline Vocals) Timelines(int frames, float original, float vocal, int gapFrom = -1, int gapTo = -1)
    {
        var o = new StemTimeline(); var v = new StemTimeline();
        for (int f = 0; f < frames; f++)
        {
            o.WriteFrame(f, original, original);
            if (f < gapFrom || f >= gapTo) v.WriteFrame(f, vocal, vocal);
        }
        return (o, v);
    }

    private static float[] RunBus(StemBus bus, StemTimeline original, StemTimeline vocals, int from, int to,
        Func<int, (float Vocal, float Music)> gains, List<int>? missed = null)
    {
        var output = new float[to - from];
        for (int f = from; f < to; f++)
        {
            var (vocal, music) = gains(f);
            if (!bus.Mix(original, vocals, f, vocal, music, out float left, out float right)) missed?.Add(f);
            Assert.Equal(left, right);
            output[f - from] = left;
        }
        return output;
    }

    private static double MaxStep(IReadOnlyList<float> x)
    {
        double max = 0;
        for (int i = 1; i < x.Count; i++) max = Math.Max(max, Math.Abs(x[i] - x[i - 1]));
        return max;
    }

    [Fact]
    public void Missing_results_fade_out_before_the_gap_and_back_in_after_it_without_a_click()
    {
        // Karaoke (vocals 0, accompaniment 100): 0.5 − 0.25 = 0.25 while results are there; frames 3000–4000 have none.
        var (original, vocals) = Timelines(8000, 0.5f, 0.25f, 3000, 4000);
        var missed = new List<int>();
        var output = RunBus(new StemBus(), original, vocals, 0, 8000, _ => (0f, 1f), missed);

        Assert.True(MaxStep(output) <= 0.25 / StemBus.FadeFrames + 1e-6, $"step {MaxStep(output)}"); // a hard mute is a 0.25 step
        Assert.Equal(0f, output[2999]); // looked ahead: silent before the first missing frame
        Assert.All(output[3000..4000], x => Assert.Equal(0f, x)); // fail closed: never the original with its vocals
        Assert.True(output[2999 - StemBus.FadeFrames] > 0.2f);
        Assert.Equal(0.25f, output[4000 + StemBus.FadeFrames + 10], 5);
        Assert.Equal(Enumerable.Range(3000, 1000), missed);
        Assert.All(output, x => Assert.True(x <= 0.25f + 1e-6f));
    }

    [Fact]
    public void Slider_moves_slew_the_gains_instead_of_stepping_them()
    {
        // Original 0.5, vocals 0.2: karaoke 0.3, full mix 0.5, vocals only 0.2. The slider jumps at frame 1000 and 3000.
        var (original, vocals) = Timelines(7000, 0.5f, 0.2f); // longer than the run: no look-ahead past its end
        var output = RunBus(new StemBus(), original, vocals, 0, 6000, f => f < 1000 ? (0f, 1f) : f < 3000 ? (1f, 1f) : (1f, 0f));
        Assert.True(MaxStep(output[300..]) <= 0.3 / StemBus.GainSlewFrames + 1e-6, $"step {MaxStep(output[300..])}"); // zipper-free
        Assert.Equal(0.3f, output[999], 5);
        Assert.Equal(0.5f, output[2999], 5);
        Assert.Equal(0.2f, output[5999], 5);
        Assert.True(output[1000 + StemBus.GainSlewFrames / 2] is > 0.35f and < 0.45f); // halfway, not jumped
    }

    [Fact]
    public void A_slider_moved_while_results_are_missing_waits_in_silence_and_never_steps()
    {
        // Full mix (no results needed) until the user picks karaoke at frame 2000, while results are missing from 1500.
        var (original, vocals) = Timelines(6000, 0.5f, 0.25f, 1500, 4000);
        var output = RunBus(new StemBus(), original, vocals, 0, 6000, f => f < 2000 ? (1f, 1f) : (0f, 1f));
        Assert.True(MaxStep(output) <= 0.5 / StemBus.FadeFrames + 1e-6, $"step {MaxStep(output)}");
        Assert.Equal(0.5f, output[1999]); // equal gains need no results: the original plays on through the gap
        Assert.All(output[(2000 + StemBus.FadeFrames)..4000], x => Assert.Equal(0f, x));
        Assert.Equal(0.25f, output[4000 + StemBus.FadeFrames + 10], 5);
    }

    // ---- The mixer with an in-process engine ----

    private sealed class Feed(LiveStemMixer mixer)
    {
        private long _tick;
        public long Frame;
        public readonly List<float> HomePod = [], Local = [];

        public Feed Init()
        {
            mixer.Clock = () => _tick;
            mixer.LocalSink = block => { for (int i = 0; i < block.Length; i += 2) Local.Add(block[i]); };
            return this;
        }

        /// <summary>Packets of 352 frames of <paramref name="signal"/>; waits for the engine after each unless it is held.</summary>
        public void Run(int packets, Func<long, float> signal, bool waitForEngine = true)
        {
            var pcm = new float[704];
            for (int p = 0; p < packets; p++)
            {
                for (int f = 0; f < 352; f++) pcm[f * 2] = pcm[f * 2 + 1] = signal(Frame + f);
                _tick++;
                mixer.Process(pcm, 352);
                for (int f = 0; f < 352; f++) { Assert.Equal(pcm[f * 2], pcm[f * 2 + 1]); HomePod.Add(pcm[f * 2]); }
                Frame += 352;
                if (waitForEngine) Assert.True(SpinWait.SpinUntil(() => mixer.Idle, 5000), "engine stalled");
            }
        }

        public void Pause(TimeSpan time) => _tick += (long)(time.TotalSeconds * Stopwatch.Frequency);
    }

    private static void WaitReady(LiveStemMixer mixer) => Assert.True(SpinWait.SpinUntil(() => mixer.Ready, 5000), "engine not ready");

    [Fact]
    public void Karaoke_on_the_HomePod_fades_around_a_slow_model_and_never_leaks_the_vocals()
    {
        var engine = new FakeStemEngine(0.5f);
        using var mixer = new LiveStemMixer(new StemSettings { HomePodVocals = 0, HomePodAccompaniment = 100 }, _ => engine);
        var feed = new Feed(mixer).Init();
        mixer.SetEnabled(true);
        WaitReady(mixer);
        feed.Run(60, _ => 0.5f);
        Assert.Equal(0.25f, feed.HomePod[^1], 5);
        Assert.Equal(0, mixer.MissedFrames); // the start-up wait is not counted
        Assert.Equal(0, mixer.DroppedBlocks);

        engine.Hold(); // a slow model: blocks queue up, then are dropped
        feed.Run(40, _ => 0.5f, waitForEngine: false);
        engine.Release();
        feed.Run(80, _ => 0.5f);

        var output = feed.HomePod;
        Assert.True(MaxStep(output) <= 0.25 / StemBus.FadeFrames + 1e-6, $"step {MaxStep(output)}");
        Assert.All(output, x => Assert.True(x <= 0.25f + 1e-6f)); // never the original (0.5): fail closed
        int silentAfterStart = output.FindIndex(60 * 352, x => x == 0f);
        Assert.True(silentAfterStart > 0, "the dropped blocks left no gap");
        Assert.True(mixer.DroppedBlocks > 0);
        Assert.True(mixer.MissedFrames > 0);
        Assert.Equal(0.25f, output[^1], 5);
    }

    [Fact]
    public void Local_stems_slew_their_gains_and_fade_around_missing_results_too()
    {
        var engine = new FakeStemEngine(0.4f); // original 0.5: vocals 0.2, accompaniment 0.3
        var settings = new StemSettings { HomePodVocals = 0, HomePodAccompaniment = 100, MonitorVocals = 100, MonitorAccompaniment = 0 };
        using var mixer = new LiveStemMixer(settings, _ => engine);
        var feed = new Feed(mixer).Init();
        mixer.SetEnabled(true);
        WaitReady(mixer);
        feed.Run(60, _ => 0.5f);
        Assert.Equal(0.2f, feed.Local[^1], 5);

        settings.MonitorVocals = 0; settings.MonitorAccompaniment = 100; // local: vocals only → accompaniment only
        settings.HomePodVocals = 100; // HomePod: karaoke → the full mix
        mixer.ApplyGains();
        int changed = feed.Local.Count;
        feed.Run(10, _ => 0.5f);
        Assert.True(MaxStep(feed.Local.Skip(changed - 1).ToList()) <= 0.1 / StemBus.GainSlewFrames + 1e-6);
        Assert.True(MaxStep(feed.HomePod.Skip(changed - 1).ToList()) <= 0.2 / StemBus.GainSlewFrames + 1e-6);
        Assert.Equal(0.3f, feed.Local[^1], 5);
        Assert.Equal(0.5f, feed.HomePod[^1], 5);

        engine.Hold();
        feed.Run(40, _ => 0.5f, waitForEngine: false);
        engine.Release();
        feed.Run(80, _ => 0.5f);
        Assert.True(MaxStep(feed.Local) <= 0.3 / StemBus.FadeFrames + 1e-6, $"step {MaxStep(feed.Local)}");
        Assert.All(feed.Local, x => Assert.True(x <= 0.3f + 1e-6f)); // never the original
        Assert.Contains(0f, feed.Local.Skip(changed + 3520));
        Assert.Equal(0.5f, feed.HomePod[^1], 5); // equal HomePod gains need no results
    }

    [Fact]
    public void Turning_separation_on_and_off_fades_between_the_paths_and_repeats_nothing()
    {
        var engine = new FakeStemEngine { NeverReady = true }; // equal gains: the mix needs no results
        using var mixer = new LiveStemMixer(new StemSettings { HomePodVocals = 100, HomePodAccompaniment = 100 }, _ => engine);
        var feed = new Feed(mixer).Init();
        static float Signal(long n) => (float)(0.3 + 0.2 * Math.Sin(2 * Math.PI * n / 4410.0)); // slow: a step is a click

        feed.Run(5, Signal, waitForEngine: false);
        Assert.Equal(Enumerable.Range(0, 1760).Select(n => Signal(n)), feed.HomePod); // off: exact pass-through
        int on = feed.HomePod.Count;
        mixer.SetEnabled(true);
        feed.Run(40, Signal, waitForEngine: false);
        var output = feed.HomePod;
        // The live path fades out over SwitchFadeFrames; the frame where it reaches 0 is the delayed path's first frame.
        int fade = LiveStemMixer.SwitchFadeFrames, first = on + fade - 1, start = first + LiveStemMixer.DelayFrames;
        for (int t = on; t < first; t++) Assert.Equal(Signal(t) * (1 - (t - on + 1.0) / fade), output[t], 1e-4);
        Assert.All(output.Skip(first).Take(LiveStemMixer.DelayFrames), x => Assert.Equal(0f, x)); // the pipeline fills
        // Then the delayed path, from the first frame the live path didn't play: nothing heard twice, nothing skipped.
        Assert.Equal(Signal(first) / StemBus.FadeFrames, output[start], 5);
        for (int t = start + StemBus.FadeFrames; t < output.Count; t++) Assert.Equal(Signal(t - LiveStemMixer.DelayFrames), output[t], 5);

        int off = output.Count;
        mixer.SetEnabled(false);
        feed.Run(10, Signal, waitForEngine: false);
        Assert.Equal(Signal(output.Count - 1), output[^1]); // live again, exactly
        Assert.Equal(0f, output[off + fade - 1]); // the delayed path faded out before the live one fades in
        Assert.True(MaxStep(output) <= 0.5 / StemBus.FadeFrames + 1e-6, $"step {MaxStep(output)}");
    }

    [Fact]
    public void After_a_pause_in_the_stream_a_pending_switch_happens_at_once_without_stale_audio()
    {
        var engine = new FakeStemEngine { NeverReady = true };
        using var mixer = new LiveStemMixer(new StemSettings { HomePodVocals = 100, HomePodAccompaniment = 100 }, _ => engine);
        var feed = new Feed(mixer).Init();
        mixer.SetEnabled(true);
        feed.Run(40, _ => 0.4f, waitForEngine: false);
        Assert.Equal(0.4f, feed.HomePod[^1], 5);
        mixer.SetEnabled(false, "with the connection");
        feed.Pause(TimeSpan.FromSeconds(1)); // reconnecting: no audio in between
        int resumed = feed.HomePod.Count;
        feed.Run(2, _ => 0.7f, waitForEngine: false);
        Assert.All(feed.HomePod.Skip(resumed), x => Assert.Equal(0.7f, x)); // the new session: live at once, nothing old
    }

    [Fact]
    public void Disabled_effect_is_exact_pass_through_and_allocates_nothing()
    {
        using var mixer = new LiveStemMixer(new StemSettings());
        float[] input = [0.2f, -0.3f, 0.1f, 0.4f]; var expected = input.ToArray();
        mixer.Process(input, 2);
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) mixer.Process(input, 2);
        Assert.Equal(start, GC.GetAllocatedBytesForCurrentThread()); Assert.Equal(expected, input);
        Assert.False(mixer.Enabled); Assert.False(mixer.MonitorOn);
    }

    [Fact]
    public void Optional_module_and_start_switches_are_not_required_or_persisted()
    {
        Assert.False(LiveStemMixer.ModuleExists(null)); Assert.False(LiveStemMixer.ModuleExists("missing-folder"));
        Assert.Throws<FileNotFoundException>(() => new LiveStemMixer(new StemSettings()).SetEnabled(true));
        string json = System.Text.Json.JsonSerializer.Serialize(new StemSettings());
        Assert.DoesNotContain("Enabled", json); Assert.DoesNotContain("MonitorOn", json);
        Assert.Equal(186, LiveStemMixer.ExtraLatencyMs);
    }

    [Fact]
    public void Module_failure_mutes_isolated_output_reports_why_and_stops_worker_without_blocking_audio()
    {
        string dir = Path.Combine(Path.GetTempPath(), "hpc-stem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Exists, but cannot execute: deterministic fail-closed exercise without any model download.
            File.WriteAllText(Path.Combine(dir, "HomePodCast.StemWorker.exe"), "invalid executable");
            File.WriteAllText(Path.Combine(dir, "vocals.onnx"), "invalid model");
            using var mixer = new LiveStemMixer(new StemSettings { ModuleDirectory = dir });
            int failed = 0;
            mixer.Failed += () => Interlocked.Increment(ref failed);
            var feed = new Feed(mixer).Init();
            mixer.SetEnabled(true);
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref failed) > 0, 5000));
            Assert.Equal(StemError.StartFailed, mixer.Error);
            Assert.Contains("HomePodCast.StemWorker.exe", mixer.ErrorDetail);
            Assert.Equal(1, Volatile.Read(ref failed));
            long started = Stopwatch.GetTimestamp();
            feed.Run(100, _ => 0.3f, waitForEngine: false);
            Assert.All(feed.HomePod, value => Assert.Equal(0f, value)); // karaoke without results: silent, never the original
            Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(1));
            Assert.Equal(0, mixer.MissedFrames); // the model never loaded: the status says so, not a counter
            mixer.SetEnabled(false, "after the module failed");
            Assert.Equal(StemError.StartFailed, mixer.Error); // 混音器 keeps saying why
            feed.Run(4, _ => 0.3f, waitForEngine: false);
            Assert.Equal(0.3f, feed.HomePod[^1]);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- The rest of the app around it ----

    [Theory]
    [InlineData(null, "{0.0.0.00000000}.{a}", "{0.0.0.00000000}.{A}", true)] // monitor = default output = captured
    [InlineData("{0.0.0.00000000}.{b}", "{0.0.0.00000000}.{a}", "{0.0.0.00000000}.{b}", true)]
    [InlineData("{0.0.0.00000000}.{b}", "{0.0.0.00000000}.{a}", "{0.0.0.00000000}.{a}", false)]
    [InlineData(null, "{0.0.0.00000000}.{a}", null, false)] // not streaming: nothing captured
    public void Local_stems_never_play_on_the_captured_device(string? monitor, string defaultOutput, string? captured, bool blocked)
    {
        Assert.Equal(blocked, LiveStemMixer.MonitorBlockedBy(true, monitor, captured, () => defaultOutput));
        Assert.False(LiveStemMixer.MonitorBlockedBy(false, monitor, captured, () => defaultOutput));
    }

    [Fact]
    public void A_monitor_on_the_captured_device_is_blocked_and_never_opened()
    {
        var engine = new FakeStemEngine { NeverReady = true };
        using var mixer = new LiveStemMixer(new StemSettings { MonitorDeviceId = "{0.0.0.00000000}.{x}" }, _ => engine);
        mixer.SetEnabled(true);
        mixer.SetCaptured("{0.0.0.00000000}.{X}");
        mixer.SetMonitor(true);
        Assert.True(mixer.MonitorOn);
        Assert.True(mixer.MonitorBlocked);
        Assert.Null(mixer.MonitorError); // no LocalMonitor was created
        mixer.SetEnabled(false);
        Assert.False(mixer.MonitorOn);
    }

    [Fact]
    public void Separation_adds_its_wait_to_the_video_delay_while_streaming()
    {
        var engine = new FakeStemEngine { NeverReady = true };
        using var mixer = new LiveStemMixer(new StemSettings(), _ => engine);
        Assert.Equal(0, mixer.AddedLatencyMs);
        mixer.SetEnabled(true);
        Assert.Equal(186, mixer.AddedLatencyMs);
        var cfg = new AppConfig();
        int plain = TrayApp.VideoDelayFor(cfg, StreamState.Streaming, Scenes.MovieMs, 0, 20, null);
        Assert.Equal(plain + 186, TrayApp.VideoDelayFor(cfg, StreamState.Streaming, Scenes.MovieMs, 0, 20, null, mixer.AddedLatencyMs));
        Assert.Equal(0, TrayApp.VideoDelayFor(cfg, StreamState.Retrying, Scenes.MovieMs, 0, 20, null, mixer.AddedLatencyMs));
    }

    [Theory]
    [InlineData(StreamState.Streaming, StreamState.Retrying, true)]
    [InlineData(StreamState.Streaming, StreamState.Idle, true)]
    [InlineData(StreamState.Streaming, StreamState.Connecting, true)]
    [InlineData(StreamState.Streaming, StreamState.Streaming, false)]
    [InlineData(StreamState.Connecting, StreamState.Streaming, false)]
    [InlineData(StreamState.Retrying, StreamState.Idle, false)]
    public void Separation_ends_with_the_connection(StreamState last, StreamState now, bool ends) =>
        Assert.Equal(ends, TrayApp.EndsStems(last, now));

    [Fact]
    public void The_sync_test_refuses_to_calibrate_the_separation_wait()
    {
        Assert.Equal(L.T("请先关闭音乐分轨再做同步校准；分轨的额外延迟会自动加入视频补偿。"), TrayApp.SyncTestRefusal(true, StreamState.Streaming));
        Assert.Equal(L.T("请先关闭音乐分轨再做同步校准；分轨的额外延迟会自动加入视频补偿。"), TrayApp.SyncTestRefusal(true, StreamState.Idle));
        Assert.Equal(L.T("请先连接音箱，再做同步测试。"), TrayApp.SyncTestRefusal(false, StreamState.Idle));
        Assert.Null(TrayApp.SyncTestRefusal(false, StreamState.Streaming));
    }

    [Fact]
    public void Every_module_failure_has_its_own_translated_reason()
    {
        var reasons = Enum.GetValues<StemError>().Where(e => e != StemError.None).Select(TrayApp.StemErrorText).ToList();
        Assert.Equal(reasons.Count, reasons.Distinct().Count());
        Assert.All(reasons, r => Assert.False(string.IsNullOrWhiteSpace(r)));
    }

    [Fact]
    public void Not_in_the_Store_version_and_the_guide_is_on_main()
    {
        Assert.False(StemPanel.Shown(packaged: true)); // Store policy 10.2.2: it starts a separately downloaded exe
        Assert.True(StemPanel.Shown(packaged: false));
        Assert.Equal("https://github.com/qiyinxi/HomePodCast/blob/main/docs/MUSIC_STEMS.md#简体中文", StemPanel.GuideUrl("zh-CN"));
        Assert.EndsWith("/blob/main/docs/MUSIC_STEMS.md#english", StemPanel.GuideUrl("de"));
    }
}

/// <summary>
/// In-process stand-in for the module: vocals = <see cref="_share"/> × the input, answered one 128-frame hop late like the
/// model, with its state reset when a block doesn't follow the previous one. <see cref="Hold"/> makes it slow.
/// </summary>
internal sealed class FakeStemEngine(float share = 0.5f) : IStemEngine
{
    private readonly float _share = share;
    private readonly float[] _history = new float[LiveStemMixer.Hop * 2];
    private readonly ManualResetEventSlim _open = new(true);
    private long _expected = -1;
    private volatile bool _aborted;

    /// <summary>The model never finishes loading (until stopped).</summary>
    public bool NeverReady { get; init; }

    public void Hold() => _open.Reset();
    public void Release() => _open.Set();

    public void Start()
    {
        while (NeverReady && !_aborted) Thread.Sleep(5);
        if (_aborted) throw new OperationCanceledException();
    }

    public void Separate(long first, ReadOnlySpan<float> input, Span<float> vocals)
    {
        while (!_open.Wait(5)) if (_aborted) throw new OperationCanceledException();
        if (first != _expected) Array.Clear(_history);
        for (int f = 0; f < LiveStemMixer.BlockFrames; f++)
            for (int ch = 0; ch < 2; ch++)
            {
                int source = f - LiveStemMixer.Hop;
                vocals[f * 2 + ch] = _share * (source < 0 ? _history[(source + LiveStemMixer.Hop) * 2 + ch] : input[source * 2 + ch]);
            }
        input[^(LiveStemMixer.Hop * 2)..].CopyTo(_history);
        _expected = first + LiveStemMixer.BlockFrames;
    }

    public void Abort()
    {
        _aborted = true;
        _open.Set();
    }

    public void Dispose() { }
}
