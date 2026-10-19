using System.Diagnostics;
using HomePodCast.Audio;
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
    private long _lastAudioRead, _maxAudioRead;
    private int _audioReads;
    private int _received;
    private readonly CompatibilityJitEvents? _jit;

    public CompatibilityDiagnostics(bool traceJit = false)
    {
        if (traceJit) _jit = new();
        Log.WarningObserved += Warning;
    }

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

    public IPreparedReceiverSession Observe(IPreparedReceiverSession session) => new ObservedSession(session, this);

    private sealed class ObservedSession(IPreparedReceiverSession inner, CompatibilityDiagnostics diagnostics) : IPreparedReceiverSession
    {
        public ReceiverProtocol Protocol => inner.Protocol;
        public bool UsesPtp => inner.UsesPtp;
        public int LatencyMs => inner.LatencyMs;
        public int? ArrivalToRenderMs => inner.ArrivalToRenderMs;
        public IReadOnlyDictionary<string, object?> Info => inner.Info;
        public Task<string> Lost => inner.Lost;
        public Exception? LostException => inner.LostException;
        public long PacketsSent => inner.PacketsSent;
        public void Start(IReceiverAudioSource source, long startAt, double percent, double cap)
            => inner.Start(new ObservedSource(source, diagnostics), startAt, percent, cap);
        public void SetVolume(double percent, double cap) => inner.SetVolume(percent, cap);
        public void StopSending() => inner.StopSending();
        public void Dispose() => inner.Dispose();
    }

    private sealed class ObservedSource(IReceiverAudioSource inner, CompatibilityDiagnostics diagnostics) : IReceiverAudioSource
    {
        public bool Read(long sendTicks, Span<float> stereo)
        {
            long before = MediaClock.Now;
            try { return inner.Read(sendTicks, stereo); }
            finally
            {
                long now = MediaClock.Now;
                Maximum(ref diagnostics._maxAudioRead, now - before);
                Interlocked.Exchange(ref diagnostics._lastAudioRead, now);
                Interlocked.Increment(ref diagnostics._audioReads);
            }
        }
        public bool ReadPacket(long ticks) => inner.ReadPacket(ticks);
        public void ReadTarget(int index, Span<float> stereo) => inner.ReadTarget(index, stereo);
        public void SkipPackets(int packets) => inner.SkipPackets(packets);
        public void Dispose() => inner.Dispose();
    }

    public void Report(string stage, long? epoch = null)
    {
        // Take the counters before formatting allocates. Reports occur before startup or after failure/cleanup.
        long now = MediaClock.Now, allocated = GC.GetTotalAllocatedBytes() - _allocated;
        double paused = (GC.GetTotalPauseDuration() - _paused).TotalMilliseconds;
        int gen0 = GC.CollectionCount(0) - _collections[0], gen1 = GC.CollectionCount(1) - _collections[1],
            gen2 = GC.CollectionCount(2) - _collections[2];
        long lastAudio = Interlocked.Read(ref _lastAudioRead);
        using var process = Process.GetCurrentProcess();
        string margin = epoch is { } start ? $", startMarginMs={MediaClock.ToMs(start - now):F1}" : "";
        Console.WriteLine($"Compatibility runtime: stage={stage}, elapsedMs={MediaClock.ToMs(now - _began):F1}, " +
            $"allocatedBytes={allocated}, gc={gen0}/{gen1}/{gen2}, gcPauseMs={paused:F1}, " +
            $"receivedBytes={Volatile.Read(ref _received)}, maxReadGapMs={MediaClock.ToMs(Interlocked.Read(ref _maxReadGap)):F1}, " +
            $"maxCollectorMs={MediaClock.ToMs(Interlocked.Read(ref _maxProcessing)):F1}, " +
            $"audioReads={Volatile.Read(ref _audioReads)}, maxAudioReadMs={MediaClock.ToMs(Interlocked.Read(ref _maxAudioRead)):F1}, " +
            $"lastAudioReadAgeMs={(lastAudio == 0 ? 0 : MediaClock.ToMs(now - lastAudio)):F1}, cpuMs={process.TotalProcessorTime.TotalMilliseconds:F1}{margin}");
        if (OperatingSystem.IsLinux() && File.Exists("/sys/fs/cgroup/cpu.stat"))
            Console.WriteLine("Compatibility cgroup CPU: " + File.ReadAllText("/sys/fs/cgroup/cpu.stat").Replace('\n', ';'));
        if (stage is "session-lost" or "completed" or "cold-stopped") _jit?.Report();
    }

    public void RequireNoSenderThreadRecompilation() => _jit!.RequireNoSenderThreadRecompilation();

    public void Dispose() { Log.WarningObserved -= Warning; _jit?.Dispose(); }
}
