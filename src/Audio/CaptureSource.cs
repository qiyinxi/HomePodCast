namespace HomePodCast.Audio;

/// <summary>
/// Something that fills the stream FIFO from the PC's audio. StreamController keeps one alive across
/// reconnects; the capture swaps its own inputs (default device, per-app routing) without touching the
/// FIFO, so the speaker connection never has to be rebuilt.
/// </summary>
public interface ICaptureSource : IDisposable
{
    string? DeviceName { get; }
    double DriftPpm { get; }
    float Peak { get; }

    /// <summary>PC-side latency added on top of plain endpoint loopback by the current capture path (ms).</summary>
    int ExtraLatencyMs { get; }

    event Action<string>? DeviceChanged;

    void Start();

    /// <summary>Longest wait between two blocks of captured audio since the last call, in ms (minute stats).</summary>
    double TakeMaxGapMs() => 0;
}
