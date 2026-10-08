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

    /// <summary>
    /// Windows has no output device at all (e.g. onboard audio "not plugged in", the virtual card uninstalled):
    /// apps have nowhere to play, so there is nothing to capture until one appears.
    /// </summary>
    bool NoOutputDevice => false;

    /// <summary>
    /// The output device chosen for capture (AppConfig.CaptureDeviceId) is unplugged, disabled or uninstalled.
    /// Nothing is captured until it is back: never the default output instead (see <see cref="CaptureEndpoint"/>).
    /// </summary>
    bool CaptureDeviceMissing => false;

    /// <summary>Id of the render endpoint being captured right now; null while there is none.</summary>
    string? EndpointId => null;
}
