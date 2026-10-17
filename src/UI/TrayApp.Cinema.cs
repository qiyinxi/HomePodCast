using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.UI;

// The wireless cinema window's side of the app (experimental since 1.1, never tested on real hardware).
internal sealed partial class TrayApp : ICinemaHost
{
    private CinemaSession? _cinemaSession;
    private bool _wantedBeforeCinema;

    private CinemaSession Cinema => _cinemaSession ??= new CinemaSession(Controller, _connection);

    /// <summary>
    /// Every connection request goes through here: the cinema's playback, if any, ends with it (it is the latest request,
    /// and LatestRequestQueue drops a cinema start still waiting).
    /// </summary>
    private void PostConnection(Action request)
    {
        if (_cinemaSession?.Supersede() == true) Log.Info("cinema: ended by another connection");
        _connection.Post(request);
    }

    IReadOnlyList<AirPlayDevice> ICinemaHost.Speakers => LastScan;
    double? ICinemaHost.Volume => Controller.Volume ?? Config.Volume;
    double ICinemaHost.VolumeCap => Config.VolumeCapPercent;
    string ICinemaHost.StatusText => Controller.StatusText;
    int ICinemaHost.LatencyMs => Controller.EffectiveLatencyMs;

    public CinemaState CinemaStateOf(CinemaFileSource source) => Cinema.StateOf(source);

    public void StartCinema(CinemaFileSource source, IReadOnlyList<CinemaSpeaker> speakers, double volume)
    {
        CinemaMatrix.Validate(speakers);
        if (!Cinema.Holding) _wantedBeforeCinema = _wantConnected;
        // The cinema retries within its own group; a network change or a wake must not start the system capture instead.
        _wantConnected = false;
        _nightDisconnected = false;
        _fx!.MicOn = false; // the microphone is mixed into the system capture, which the cinema replaces
        volume = VolumeLimit.Clamp(volume, Config.VolumeCapPercent);
        int latency = Math.Max(Config.LatencyMs, 200);
        Log.Info($"cinema: start {CinemaChannels.Describe(source.Channels, source.Mask)} {source.SourceRate} Hz on {speakers.Count} speakers " +
                 $"({string.Join(", ", speakers.Select(s => $"{s.Role} {Log.Name(s.Name)} +{s.DelayMs} ms"))}), volume {volume:F0} %, " +
                 $"latency {latency} ms; {source.Routing}");
        Cinema.Start(source, speakers, latency, volume);
    }

    public void StopCinema(CinemaFileSource source, string reason)
    {
        if (!Cinema.Stop(source)) _wantConnected = _wantedBeforeCinema; // its start never ran: what played before goes on
        Log.Info($"cinema: stopped ({reason}) at {source.PositionSeconds:F1} s");
    }

    public IDisposable LimitVolumeForMeasurement()
    {
        long version = Controller.SetScopedMeasurementCap(AcousticAlignment.MeasurementVolumeCapPercent);
        return new OnDispose(() => Controller.EndMeasurementCap(version));
    }

    private sealed class OnDispose(Action action) : IDisposable
    {
        private Action? _action = action;
        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}

/// <summary>What the cinema window needs from the app (TrayApp; the tests give it a fake).</summary>
internal interface ICinemaHost
{
    AppConfig Config { get; }

    /// <summary>The last mDNS scan (HomePods are offered as speakers).</summary>
    IReadOnlyList<AirPlayDevice> Speakers { get; }

    /// <summary>The volume the app shows: the speaker's, or the saved one.</summary>
    double? Volume { get; }

    double VolumeCap { get; }
    string StatusText { get; }
    int LatencyMs { get; }

    CinemaState CinemaStateOf(CinemaFileSource source);

    /// <param name="volume">The window's volume slider: the cinema starts at this volume.</param>
    void StartCinema(CinemaFileSource source, IReadOnlyList<CinemaSpeaker> speakers, double volume);

    /// <summary>Stops the cinema's own playback; never anything another connection started.</summary>
    void StopCinema(CinemaFileSource source, string reason);

    /// <summary>A volume slider moves (sent at once); see <see cref="ApplyVolume"/>.</summary>
    void PreviewVolume(double percent);

    /// <summary>The slider's debounce: saved and sent like 首页's volume, and the other volume controls follow.</summary>
    void ApplyVolume(double percent);

    /// <summary>The speakers play at most 25 % until the returned object is disposed.</summary>
    IDisposable LimitVolumeForMeasurement();
}
