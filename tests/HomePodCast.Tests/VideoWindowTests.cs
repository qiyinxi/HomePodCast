using HomePodCast.Players;
using HomePodCast.UI;
using HomePodCast.VideoWindow;

namespace HomePodCast.Tests;

/// <summary>
/// The synchronized viewing window's logic without a window, a GPU or a capture (modules/VideoWindow): the delay queue,
/// which captured frames it keeps (FrameCadence), when the presenter shows them, the display latency it subtracts, the
/// local status it follows, and how HomePodCast starts it. The GPU path itself: the module's --self-test (build job);
/// the window: gui-smoke.
/// </summary>
public class VideoWindowTests
{
    private sealed class Frame(long time = 0) : IDisposable
    {
        public long Time { get; } = time;
        public int Disposals;
        public void Dispose() => Disposals++;
    }

    [Fact]
    public void Only_due_frames_are_displayed_and_latest_due_frame_owns_the_image()
    {
        using var queue = new DelayQueue<Frame>(100, 8);
        var a = new Frame(); var b = new Frame(); var c = new Frame();
        queue.Add(100, 10, a); queue.Add(200, 10, b); queue.Add(300, 10, c);
        Assert.Null(queue.TakeReady(149, 50));
        Assert.Same(b, queue.TakeReady(260, 50)); Assert.Equal(1, a.Disposals); Assert.Equal(0, b.Disposals);
        Assert.Equal((1, 10L), queue.Buffered); Assert.Null(queue.TakeReady(349, 50));
        Assert.Same(c, queue.TakeReady(350, 50)); b.Dispose(); c.Dispose();
    }

    [Fact]
    public void Memory_pressure_and_invalid_frames_never_bypass_delay_or_leak_ownership()
    {
        using var queue = new DelayQueue<Frame>(20, 2);
        var a = new Frame(); var b = new Frame(); var c = new Frame(); var stale = new Frame(); var huge = new Frame();
        queue.Add(10, 10, a); queue.Add(20, 10, b); queue.Add(30, 10, c);
        queue.Add(25, 10, stale); queue.Add(40, 21, huge);
        Assert.Equal(1, a.Disposals); Assert.Equal(1, stale.Disposals); Assert.Equal(1, huge.Disposals);
        Assert.Null(queue.TakeReady(50, 40)); Assert.Equal((2, 20L), queue.Buffered);
        queue.Clear(); Assert.Equal(1, b.Disposals); Assert.Equal(1, c.Disposals);
        var restart = new Frame(); queue.Add(1, 10, restart); queue.Dispose(); Assert.Equal(1, restart.Disposals);
        var after = new Frame(); queue.Add(2, 10, after); Assert.Equal(1, after.Disposals);
    }

    [Fact]
    public void Delay_changes_do_not_display_a_future_frame_and_zero_delay_catches_up()
    {
        using var queue = new DelayQueue<Frame>(100, 8);
        var old = new Frame(); var next = new Frame(); queue.Add(100, 10, old); queue.Add(200, 10, next);
        Assert.Null(queue.TakeReady(250, 200)); Assert.Same(next, queue.TakeReady(250, 0));
        Assert.Equal(1, old.Disposals); next.Dispose();
    }

    // ---------------------------------------------------------------- which captured frames are kept

    /// <summary>
    /// Capture timestamps (100 ns) of <paramref name="fps"/> content on a <paramref name="hz"/> screen: a content frame
    /// shows from the first refresh after it is presented (two in one refresh: only the later one is ever composed), and
    /// the stamps jitter by up to <paramref name="jitterMs"/>.
    /// </summary>
    private static List<long> Captured(double fps, double hz, double seconds, double jitterMs = 0.2, int seed = 7)
    {
        var random = new Random(seed);
        double refresh = 1000.0 / hz, period = 1000.0 / fps;
        var times = new List<long>();
        long lastRefresh = -1;
        for (int k = 0; k * period < seconds * 1000; k++)
        {
            long n = (long)Math.Ceiling((k * period + 0.37 * refresh) / refresh);
            if (n == lastRefresh) continue;
            lastRefresh = n;
            times.Add(10_000_000 + (long)((n * refresh + (random.NextDouble() * 2 - 1) * jitterMs) * Clock.TicksPerMs));
        }
        return times;
    }

    private static List<long> Kept(IEnumerable<long> times, long interval = FrameCadence.Interval30)
    {
        var cadence = new FrameCadence();
        return times.Where(t => cadence.Accept(t, interval)).ToList();
    }

    private static double Fps(IReadOnlyList<long> times) => (times.Count - 1) / ((times[^1] - times[0]) / 1e7);

    [Fact]
    public void Thirty_fps_content_keeps_thirty_fps_on_a_60_1_Hz_screen()
    {
        // 2026-10-13 review of PR #23: the first version's hard "33.33 ms since the last kept frame" saw these frames
        // 33.28 ms apart (two refreshes of 16.64 ms) and dropped most of them.
        var captured = Captured(fps: 30, hz: 60.1, seconds: 60);
        var kept = Kept(captured);
        Assert.Equal(captured.Count, kept.Count);
        Assert.InRange(Fps(kept), 29.9, 30.1);
        Assert.All(kept.Zip(kept.Skip(1), (a, b) => (b - a) / 1e4), gap => Assert.InRange(gap, 32.8, 50.4));

        long last = 0;
        var hardGate = captured.Where(t => { if (t - last < FrameCadence.Interval30) return false; last = t; return true; }).ToList();
        Assert.True(Fps(hardGate) < 20, $"the old gate kept {Fps(hardGate):F1} fps");
    }

    [Theory]
    [InlineData(30, 59.94)]
    [InlineData(30, 50)]
    [InlineData(30, 144)]
    [InlineData(25, 50)]
    [InlineData(24, 60)]
    [InlineData(23.976, 60)]
    public void Content_no_faster_than_30_fps_keeps_every_frame(double fps, double hz)
    {
        var captured = Captured(fps, hz, seconds: 30);
        Assert.Equal(captured.Count, Kept(captured).Count);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(60.1)]
    [InlineData(75)]
    [InlineData(90)]
    [InlineData(100)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(165)]
    [InlineData(240)]
    public void Faster_content_is_thinned_to_an_even_30_fps(double hz)
    {
        var kept = Kept(Captured(fps: hz, hz: hz, seconds: 30));
        Assert.InRange(Fps(kept), 29.5, 30.05);
        double refreshMs = 1000 / hz;
        // After the first second, every gap is the interval within one refresh: no alternating short and long frames.
        var steady = kept.Where(t => t - kept[0] > 10_000_000).ToList();
        Assert.All(steady.Zip(steady.Skip(1), (a, b) => (b - a) / 1e4), gap => Assert.InRange(gap, 33.33 - refreshMs - 0.5, 33.33 + refreshMs + 0.5));
    }

    [Fact]
    public void A_pause_in_the_content_restarts_the_schedule_without_a_burst()
    {
        var cadence = new FrameCadence();
        long t = 10_000_000, refresh = 166_389;
        Assert.True(cadence.Accept(t, FrameCadence.Interval30));
        Assert.True(cadence.Accept(t + 2 * refresh, FrameCadence.Interval30));
        // Two seconds later the video plays on at 60 fps: kept again at once, not a catch-up run of the missed slots;
        // like at the start of a capture, the schedule settles after one short gap, then every other frame.
        long resume = t + 20_000_000;
        var kept = Enumerable.Range(0, 60).Select(i => resume + i * refresh).Where(x => cadence.Accept(x, FrameCadence.Interval30)).ToList();
        Assert.Equal(resume, kept[0]);
        Assert.All(kept.Skip(1).Zip(kept.Skip(2), (a, b) => b - a), gap => Assert.Equal(2 * refresh, gap));
        Assert.InRange(kept.Count, 30, 31);
    }

    [Fact]
    public void Long_delays_thin_the_frames_out_to_stay_within_the_queue()
    {
        const long bytes = 1920L * 1080 * 4, limit = 256L * 1024 * 1024;
        Assert.Equal(FrameCadence.Interval30, FrameCadence.IntervalFor(300, bytes, limit, 64));
        Assert.Equal(FrameCadence.Interval30, FrameCadence.IntervalFor(800, bytes, limit, 64));
        foreach (int delay in new[] { 1000, 1500, 2000 })
        {
            long interval = FrameCadence.IntervalFor(delay, bytes, limit, 64);
            Assert.True(interval > FrameCadence.Interval30);
            long frames = (delay + 100L) * Clock.TicksPerMs / interval;
            Assert.True(frames <= limit / bytes - 2, $"{delay} ms needs {frames} frames");
        }
        // Small frames: the frame limit counts.
        Assert.Equal(FrameCadence.Interval30, FrameCadence.IntervalFor(1500, 100, limit, 64));
        Assert.True(FrameCadence.IntervalFor(2000, 100, limit, 64) > FrameCadence.Interval30);
    }

    // ---------------------------------------------------------------- when the presenter shows them

    [Fact]
    public void Presenting_at_60_1_Hz_shows_every_30_fps_frame_on_time()
    {
        const long delay = 300 * Clock.TicksPerMs;
        double refreshMs = 1000 / 60.1;
        long refresh = (long)(refreshMs * Clock.TicksPerMs);
        var captured = Kept(Captured(fps: 30, hz: 60.1, seconds: 20));
        using var queue = new DelayQueue<Frame>(256L * 1024 * 1024, 64);
        var latency = new DisplayLatency();
        var shown = new List<(Frame Frame, long VisibleAt)>();
        int next = 0;
        // The presenter wakes at every refresh; this present is on screen one refresh later.
        for (long wake = captured[0]; wake < captured[^1] + delay; wake += refresh)
        {
            while (next < captured.Count && captured[next] <= wake) { queue.Add(captured[next], 1920 * 1080 * 4, new Frame(captured[next])); next++; }
            latency.Woke(wake);
            long lead = latency.Ticks;
            if (queue.TakeReady(wake + lead, delay) is { } frame) shown.Add((frame, wake + refresh));
        }
        Assert.InRange(latency.RefreshTicks, refresh - 1000, refresh + 1000);
        // Every frame, none skipped (each lasts two refreshes, now and then three), all within one refresh of capture + delay.
        Assert.Equal(captured.Count, shown.Count);
        Assert.Equal(captured.Count, shown.Select(s => s.Frame).Distinct().Count());
        Assert.All(shown, s => Assert.Equal(0, s.Frame.Disposals));
        Assert.All(shown, s => Assert.InRange((s.VisibleAt - s.Frame.Time - delay) / 1e4, -0.5, refreshMs + 0.5));
    }

    // ---------------------------------------------------------------- the display latency subtracted

    [Fact]
    public void Display_latency_is_one_refresh_until_frame_statistics_measure_it()
    {
        var latency = new DisplayLatency();
        Assert.Equal(166_667, latency.Ticks);
        long t = 1_000_000_000, refresh = 69_444; // 144 Hz
        for (int i = 0; i < 20; i++) latency.Woke(t + i * refresh);
        Assert.InRange(latency.Ticks, refresh - 100, refresh + 100);

        // A missed refresh (two periods) is not a slower screen.
        latency.Woke(t + 21 * refresh);
        Assert.InRange(latency.RefreshTicks, refresh - 100, refresh + 100);

        // Each present reaches the screen 21 ms after its frame was chosen.
        for (uint p = 1; p <= 7; p++) { latency.Presented(p, t + p * refresh); latency.Displayed(p, t + p * refresh + 210_000); }
        Assert.False(latency.Measured);
        latency.Presented(8, t + 8 * refresh); latency.Displayed(8, t + 8 * refresh + 210_000);
        Assert.True(latency.Measured);
        Assert.InRange(latency.Ticks, 209_000, 211_000);

        // The statistics repeat the last shown present until the next one: counted once. Unknown presents count not at all.
        latency.Displayed(8, t + 8 * refresh + 900_000);
        latency.Displayed(99, t + 900_000);
        Assert.Equal(8, latency.Samples);
        // An impossible value (longer than 100 ms) is ignored.
        latency.Presented(9, t); latency.Displayed(9, t + 5_000_000);
        Assert.Equal(8, latency.Samples);
    }

    [Fact]
    public void Moving_to_a_slower_screen_is_taken_over_after_a_while()
    {
        var latency = new DisplayLatency();
        long t = 1_000_000_000;
        for (int i = 0; i < 20; i++) latency.Woke(t + i * 69_444);         // 144 Hz
        t += 20 * 69_444;
        for (int i = 1; i <= 40; i++) latency.Woke(t + i * 166_667);       // then 60 Hz
        Assert.InRange(latency.RefreshTicks, 160_000, 170_000);
    }

    // ---------------------------------------------------------------- the local status

    [Fact]
    public void Local_status_controls_delay_only_for_the_expected_streaming_service()
    {
        Assert.Equal(new LocalStatus(356, "1.1.0"), StatusDelay.Read("{\"app\":\"HomePodCast\",\"version\":\"1.1.0\",\"streaming\":true,\"videoDelayMs\":356}"));
        Assert.Equal(new LocalStatus(0, null), StatusDelay.Read("{\"app\":\"HomePodCast\",\"streaming\":false}"));
        Assert.Throws<InvalidDataException>(() => StatusDelay.Read("{\"app\":\"other\",\"streaming\":false}"));
        Assert.Throws<InvalidDataException>(() => StatusDelay.Read("{\"app\":\"HomePodCast\",\"streaming\":true,\"videoDelayMs\":2001}"));
        Assert.Equal(0, StatusDelay.Effective(10, -20)); Assert.Equal(2000, StatusDelay.Effective(1900, 200));
    }

    [Fact]
    public void The_module_warns_when_its_version_differs_from_the_app()
    {
        // The status reports the app's X.Y.Z; the module's assembly version comes from the same -p:Version of a build.
        Assert.True(StatusDelay.SameVersion("1.1.0", new Version(1, 1, 0, 0)));
        Assert.True(StatusDelay.SameVersion("1.1.0-beta.3", new Version(1, 1, 0)));
        Assert.False(StatusDelay.SameVersion("1.1.1", new Version(1, 1, 0, 0)));
        Assert.False(StatusDelay.SameVersion("1.2.0", new Version(1, 1, 0, 0)));
        Assert.False(StatusDelay.SameVersion(null, new Version(1, 1, 0)));
        Assert.False(StatusDelay.SameVersion("x", new Version(1, 1, 0)));
        // What the app reports is what the module compares.
        var reported = TrayApp.ApiStatus(true, "HomePod", 120, 300, false);
        string json = System.Text.Json.JsonSerializer.Serialize(reported);
        Assert.True(StatusDelay.SameVersion(StatusDelay.Read(json).Version, typeof(TrayApp).Assembly.GetName().Version));
    }

    // ---------------------------------------------------------------- how HomePodCast starts it

    [Fact]
    public void Companion_launcher_uses_literal_arguments_without_shell_or_capture_autostart()
    {
        var start = VideoWindowModule.StartInfo(Path.Combine(Path.GetTempPath(), "module space"), 47100, 350, "zh-CN");
        Assert.False(start.UseShellExecute); Assert.EndsWith(VideoWindowModule.Executable, start.FileName);
        Assert.Equal(new[] { "--status-port", "47100", "--delay-ms", "350", "--language", "zh-CN" }, start.ArgumentList);
        Assert.False(VideoWindowModule.Exists(null)); Assert.False(VideoWindowModule.Exists("missing-module"));
        Assert.Null(VideoWindowModule.Version("missing-module"));
    }

    [Fact]
    public void The_Microsoft_Store_version_does_not_offer_the_module()
    {
        // Store policy 10.2.2: the packaged app must not start an exe downloaded apart from its package.
        Assert.False(VideoWindowModule.OfferedIn(packaged: true));
        Assert.True(VideoWindowModule.OfferedIn(packaged: false));
    }
}
