namespace HomePodCast.Update;

/// <summary>
/// When automatic checks run (only when turned on in 设置, and only in release builds): about 30 s after startup,
/// then every 24 hours. The last successful check is saved, so a restart does not check again sooner; a check that
/// failed (offline, rate limit) is tried again after an hour. Times are UTC, compared with the wall clock, so sleep
/// and hibernation don't stretch the interval.
/// </summary>
internal static class UpdateSchedule
{
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);

    /// <summary>The timer looks at the clock at least this often (a long timer would not notice sleep or a clock change).</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(10);

    /// <summary>
    /// When the next check is due after a start at <paramref name="nowUtc"/>: 24 hours after the last one, but not
    /// before the startup delay. A last check "in the future" (the clock was set back) counts as none.
    /// </summary>
    public static DateTime FirstDue(DateTime? lastCheckUtc, DateTime nowUtc)
    {
        var earliest = nowUtc + StartupDelay;
        if (lastCheckUtc is not { } last || last > nowUtc) return earliest;
        var due = last + Interval;
        return due > earliest ? due : earliest;
    }

    /// <summary>How long the timer waits before it looks again (at most <see cref="MaxWait"/>, at least a second).</summary>
    public static TimeSpan Wait(DateTime dueUtc, DateTime nowUtc)
    {
        var wait = dueUtc - nowUtc;
        if (wait < TimeSpan.FromSeconds(1)) return TimeSpan.FromSeconds(1);
        return wait > MaxWait ? MaxWait : wait;
    }
}
