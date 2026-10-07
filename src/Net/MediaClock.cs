using System.Diagnostics;

namespace HomePodCast.Net;

/// <summary>
/// One monotonic timebase (QPC) for everything: packet deadlines, sync packets and NTP replies.
/// NTP time is anchored to the wall clock once at startup so wall-clock adjustments can't make
/// the stream jump.
/// </summary>
public static class MediaClock
{
    private static readonly long QpcBase = Stopwatch.GetTimestamp();
    private static readonly ulong NtpBase = FromDateTime(DateTime.UtcNow);

    public static long Frequency => Stopwatch.Frequency;
    public static long Now => Stopwatch.GetTimestamp();

    public static long FromMs(double ms) => (long)(ms * Frequency / 1000.0);
    public static double ToMs(long ticks) => ticks * 1000.0 / Frequency;

    /// <summary>64-bit NTP timestamp (seconds since 1900 in the high word) for a QPC tick value.</summary>
    public static ulong NtpAt(long qpc)
    {
        long delta = qpc - QpcBase;
        UInt128 frac = ((UInt128)(ulong)Math.Abs(delta) << 32) / (ulong)Frequency;
        return delta >= 0 ? NtpBase + (ulong)frac : NtpBase - (ulong)frac;
    }

    public static ulong NtpNow() => NtpAt(Now);

    private static ulong FromDateTime(DateTime utc)
    {
        var since1900 = utc - new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        ulong seconds = (ulong)since1900.Ticks / TimeSpan.TicksPerSecond;
        ulong remTicks = (ulong)since1900.Ticks % TimeSpan.TicksPerSecond;
        return seconds << 32 | (ulong)(((UInt128)remTicks << 32) / TimeSpan.TicksPerSecond);
    }
}
