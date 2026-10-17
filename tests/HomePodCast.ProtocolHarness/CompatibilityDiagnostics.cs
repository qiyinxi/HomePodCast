using HomePodCast.Net;

namespace HomePodCast.ProtocolHarness;

// Keep diagnostics out of the sender: observing a failed compatibility run must not relax its deadlines.
internal sealed class CompatibilityDiagnostics : IDisposable
{
    private readonly long _began = MediaClock.Now;
    private readonly long _allocated = GC.GetTotalAllocatedBytes();
    private readonly TimeSpan _paused = GC.GetTotalPauseDuration();
    private readonly int[] _collections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
    private long _lastRead, _maxReadGap, _maxProcessing;
    private int _received;

    public CompatibilityDiagnostics() => Log.WarningObserved += Warning;

    public void ObserveRead(int received, long completed, long processed)
    {
        long previous = Interlocked.Exchange(ref _lastRead, completed);
        if (previous != 0) Maximum(ref _maxReadGap, completed - previous);
        Maximum(ref _maxProcessing, processed - completed);
        Volatile.Write(ref _received, received);
    }

    private static void Maximum(ref long value, long candidate)
    {
        long previous;
        do { previous = Volatile.Read(ref value); if (candidate <= previous) return; }
        while (Interlocked.CompareExchange(ref value, candidate, previous) != previous);
    }

    private void Warning(string message)
    {
        if (message.StartsWith("session lost: ", StringComparison.Ordinal)) Report("session-lost");
    }

    public void Report(string stage, long? epoch = null)
    {
        // Take the counters before formatting allocates. Reports occur before startup or after failure/cleanup.
        long now = MediaClock.Now, allocated = GC.GetTotalAllocatedBytes() - _allocated;
        double paused = (GC.GetTotalPauseDuration() - _paused).TotalMilliseconds;
        int gen0 = GC.CollectionCount(0) - _collections[0], gen1 = GC.CollectionCount(1) - _collections[1],
            gen2 = GC.CollectionCount(2) - _collections[2];
        string margin = epoch is { } start ? $", startMarginMs={MediaClock.ToMs(start - now):F1}" : "";
        Console.WriteLine($"Compatibility runtime: stage={stage}, elapsedMs={MediaClock.ToMs(now - _began):F1}, " +
            $"allocatedBytes={allocated}, gc={gen0}/{gen1}/{gen2}, gcPauseMs={paused:F1}, " +
            $"receivedBytes={Volatile.Read(ref _received)}, maxReadGapMs={MediaClock.ToMs(Interlocked.Read(ref _maxReadGap)):F1}, " +
            $"maxCollectorMs={MediaClock.ToMs(Interlocked.Read(ref _maxProcessing)):F1}{margin}");
    }

    public void Dispose() => Log.WarningObserved -= Warning;
}
