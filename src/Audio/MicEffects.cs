using System.Text.Json.Serialization;

namespace HomePodCast.Audio;

[JsonConverter(typeof(JsonStringEnumConverter<MicDestination>))]
public enum MicDestination { HomePod, Monitor, Both }

/// <summary>Saved settings of the mic and effects chain (AppConfig.Effects). The mic's on/off is not saved.</summary>
public sealed class EffectsSettings
{
    /// <summary>Input device id; null = the default input.</summary>
    public string? MicDeviceId { get; set; }
    public double MicGainDb { get; set; }
    public bool GateEnabled { get; set; }
    public double GateThresholdDb { get; set; } = -50;
    public MicDestination Destination { get; set; } = MicDestination.HomePod;

    /// <summary>Output device id for 本机监听; null = the default output.</summary>
    public string? MonitorDeviceId { get; set; }

    public bool ReverbEnabled { get; set; }
    public double ReverbRoomSize { get; set; } = 0.5;
    public double ReverbDamping { get; set; } = 0.5;
    public double ReverbMix { get; set; } = 0.25;
    public double ReverbPreDelayMs { get; set; } = 20;

    /// <summary>EQ on the mic only (before the reverb).</summary>
    public EqSettings MicEq { get; set; } = new();

    /// <summary>EQ on everything sent to the HomePod.</summary>
    public EqSettings OutputEq { get; set; } = new();
}

public sealed class EqSettings
{
    /// <summary>"flat", "bass-boost", "vocal-boost", "reduce-bass" or "custom".</summary>
    public string Preset { get; set; } = "flat";

    /// <summary>Custom band gains in dB at 60 / 250 / 1k / 3.5k / 12k Hz, used when Preset is "custom".</summary>
    public double[] Gains { get; set; } = new double[Equalizer.BandCount];
}

/// <summary>
/// The microphone and effects chain as one unit for the app to own (create once, keep for the app's
/// lifetime): mic → gain / noise gate → <see cref="MicEq"/> → <see cref="Reverb"/> → <see cref="HomePodSource"/>
/// (pulled by the stream mixer) and/or a <see cref="LocalMonitor"/>; plus <see cref="OutputEq"/> for the whole
/// HomePod output, to be run in the send path. The UI edits <see cref="Settings"/> and calls <see cref="Apply"/>.
/// </summary>
public sealed class MicEffects : IDisposable
{
    private readonly object _lock = new();
    private MicCapture? _mic;
    private LocalMonitor? _monitor;
    private bool _micOn;
    private string? _outputPresetOverride;

    public EffectsSettings Settings { get; }
    public Equalizer MicEq { get; } = new(MicCapture.OutputRate);
    public Reverb Reverb { get; } = new(MicCapture.OutputRate);

    /// <summary>EQ for everything sent to the HomePod: call OutputEq.Process on the mixed 44.1 kHz stereo before sending.</summary>
    public Equalizer OutputEq { get; } = new(MicCapture.OutputRate);

    /// <summary>
    /// The processed mic for the stream mixer, 44.1 kHz interleaved stereo: Read(span) returns the frames of
    /// real audio and zero-fills the rest (silence while the mic is off or not routed to the HomePod).
    /// The same object for the app's lifetime.
    /// </summary>
    public AudioTap HomePodSource { get; } = new(MicCapture.OutputRate, MicCapture.OutputRate, targetMs: 20, capMs: 80);

    public MicCapture? Mic => _mic;
    public LocalMonitor? Monitor => _monitor;

    /// <summary>Raised from audio threads when the mic or the monitor opens, fails or closes.</summary>
    public event Action? Changed;

    public MicEffects(EffectsSettings settings)
    {
        Settings = settings;
        Apply();
    }

    /// <summary>Turns the microphone on or off (not saved: the mic is always off when the app starts).</summary>
    public bool MicOn
    {
        get => _micOn;
        set
        {
            _micOn = value;
            Apply();
        }
    }

    /// <summary>
    /// A preset id that temporarily replaces the saved output EQ (e.g. a night mode sets "reduce-bass"
    /// together with its compressor); null restores the saved one. The switch glides, no level jump.
    /// </summary>
    public string? OutputPresetOverride
    {
        get => _outputPresetOverride;
        set
        {
            _outputPresetOverride = value;
            Apply();
        }
    }

    /// <summary>Push the settings to the effects and start/stop/switch the mic and the monitor.</summary>
    public void Apply()
    {
        lock (_lock)
        {
            var s = Settings;
            s.MicEq ??= new EqSettings();
            s.OutputEq ??= new EqSettings();
            MicEq.Set(s.MicEq.Preset, s.MicEq.Gains ?? []);
            if (_outputPresetOverride != null) OutputEq.SetPreset(_outputPresetOverride);
            else OutputEq.Set(s.OutputEq.Preset, s.OutputEq.Gains ?? []);
            Reverb.Enabled = s.ReverbEnabled;
            Reverb.RoomSize = (float)s.ReverbRoomSize;
            Reverb.Damping = (float)s.ReverbDamping;
            Reverb.Mix = (float)s.ReverbMix;
            Reverb.PreDelayMs = (float)s.ReverbPreDelayMs;

            if (!_micOn)
            {
                StopMonitor();
                StopMic();
                return;
            }

            if (_mic == null || _mic.DeviceId != Id(s.MicDeviceId))
            {
                StopMonitor();
                StopMic();
                _mic = new MicCapture(s.MicDeviceId) { Effects = [MicEq, Reverb] };
                _mic.StatusChanged += RaiseChanged;
                _mic.Start();
            }
            _mic.GainDb = (float)s.MicGainDb;
            _mic.GateEnabled = s.GateEnabled;
            _mic.GateThresholdDb = (float)s.GateThresholdDb;

            if (s.Destination != MicDestination.Monitor) _mic.Attach(HomePodSource);
            else _mic.Detach(HomePodSource);

            if (s.Destination != MicDestination.HomePod)
            {
                if (_monitor == null || _monitor.DeviceId != Id(s.MonitorDeviceId))
                {
                    StopMonitor();
                    _monitor = new LocalMonitor(_mic, s.MonitorDeviceId);
                    _monitor.StatusChanged += RaiseChanged;
                    _monitor.Start();
                }
            }
            else
            {
                StopMonitor();
            }
        }
    }

    private static string? Id(string? id) => string.IsNullOrEmpty(id) ? null : id;

    private void RaiseChanged() => Changed?.Invoke();

    private void StopMic()
    {
        if (_mic == null) return;
        _mic.Detach(HomePodSource);
        _mic.StatusChanged -= RaiseChanged;
        _mic.Dispose();
        _mic = null;
        RaiseChanged();
    }

    private void StopMonitor()
    {
        if (_monitor == null) return;
        _monitor.StatusChanged -= RaiseChanged;
        _monitor.Dispose();
        _monitor = null;
        RaiseChanged();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _micOn = false;
            StopMonitor();
            StopMic();
        }
    }
}
