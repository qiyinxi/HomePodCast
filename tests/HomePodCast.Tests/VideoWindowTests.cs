using HomePodCast.Players;
using HomePodCast.VideoWindow;

namespace HomePodCast.Tests;

public class VideoWindowTests
{
    private sealed class Frame : IDisposable { public int Disposals; public void Dispose() => Disposals++; }

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

    [Fact]
    public void Local_status_controls_delay_only_for_the_expected_streaming_service()
    {
        Assert.Equal(356, StatusDelay.Read("{\"app\":\"HomePodCast\",\"streaming\":true,\"videoDelayMs\":356}"));
        Assert.Equal(0, StatusDelay.Read("{\"app\":\"HomePodCast\",\"streaming\":false}"));
        Assert.Throws<InvalidDataException>(() => StatusDelay.Read("{\"app\":\"other\",\"streaming\":false}"));
        Assert.Throws<InvalidDataException>(() => StatusDelay.Read("{\"app\":\"HomePodCast\",\"streaming\":true,\"videoDelayMs\":2001}"));
        Assert.Equal(0, StatusDelay.Effective(10, -20)); Assert.Equal(2000, StatusDelay.Effective(1900, 200));
    }

    [Fact]
    public void Companion_launcher_uses_literal_arguments_without_shell_or_capture_autostart()
    {
        var start = VideoWindowModule.StartInfo(Path.Combine(Path.GetTempPath(), "module space"), 47100, 350, "zh-CN");
        Assert.False(start.UseShellExecute); Assert.EndsWith(VideoWindowModule.Executable, start.FileName);
        Assert.Equal(new[] { "--status-port", "47100", "--delay-ms", "350", "--language", "zh-CN" }, start.ArgumentList);
        Assert.False(VideoWindowModule.Exists(null)); Assert.False(VideoWindowModule.Exists("missing-module"));
    }
}
