using System.Diagnostics;

namespace HomePodCast.VideoWindow;

/// <summary>
/// The one time base of the viewing window: QueryPerformanceCounter in 100 ns ticks, which is what
/// Windows.Graphics.Capture stamps its frames with (SystemRelativeTime) and DXGI its frame statistics (SyncQPCTime).
/// </summary>
internal static class Clock
{
    public const long TicksPerMs = 10_000;

    public static long Now => FromQpc(Stopwatch.GetTimestamp());

    public static long FromQpc(long qpc) => (long)(qpc * (10_000_000.0 / Stopwatch.Frequency));
}
