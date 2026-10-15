namespace HomePodCast.VideoWindow;

/// <summary>
/// Which captured frames the delay queue keeps: on average at most one per interval (30 fps, fewer for long delays),
/// without dropping frames of content that is no faster than that.
/// Windows.Graphics.Capture stamps a frame with the composition it appeared in, so 30 fps video on a 60.1 Hz screen
/// arrives 33.28 ms apart and now and then 49.9 ms, never exactly 33.33 ms: the first version's hard "at least 33.33 ms
/// since the last kept frame" dropped most of those frames (15–20 fps, uneven). Here each kept frame advances a schedule
/// by one interval, and a frame is kept when it comes no earlier than 70 % of an interval before its slot.
/// </summary>
internal sealed class FrameCadence
{
    /// <summary>30 fps in 100 ns ticks.</summary>
    public const long Interval30 = 333_333;

    // 70 %: above the one-refresh jitter of 30 fps content on 50–60 Hz screens (a frame up to 20 ms early), and between
    // the two candidates of faster sources, so timestamp jitter doesn't alternate the choice (kept / not kept, early by:
    // 60 Hz 16.7 / 33.3 ms, 90 Hz 22.2 / 33.3, 120 Hz 16.7 / 25, 144 Hz 20.8 / 27.8, 165 Hz 18.2 / 24.2; threshold 23.3 ms).
    private const double EarlyFraction = 0.7;

    private long _next = long.MinValue;

    /// <summary>A new source or size: the next frame starts the schedule.</summary>
    public void Reset() => _next = long.MinValue;

    /// <param name="time">The frame's capture time, 100 ns ticks.</param>
    /// <param name="interval">At most one frame per this many ticks on average.</param>
    public bool Accept(long time, long interval)
    {
        if (_next != long.MinValue && time < _next - (long)(interval * EarlyFraction)) return false;
        // A frame far behind its slot (the content paused or dropped frames) restarts the schedule at itself.
        _next = _next == long.MinValue || time > _next + interval / 2 ? time + interval : _next + interval;
        return true;
    }

    /// <summary>
    /// The interval that keeps the queue within its limits for <paramref name="delayMs"/>: 30 fps while the delay plus
    /// 100 ms of margin fits, fewer frames per second beyond that (a still picture now and then rather than none).
    /// </summary>
    public static long IntervalFor(int delayMs, long frameBytes, long byteLimit, int frameLimit)
    {
        // Two frames of the limits stay free: the one on screen and one arriving while the oldest is taken.
        long fit = Math.Min(frameLimit, byteLimit / Math.Max(1, frameBytes)) - 2;
        long span = (Math.Max(0, delayMs) + 100L) * Clock.TicksPerMs;
        return fit < 1 ? Math.Max(Interval30, span) : Math.Max(Interval30, span / fit);
    }
}
