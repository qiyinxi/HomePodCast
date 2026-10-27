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

    /// <summary>Low cut (high-pass) in front of the mic EQ, against rumble, handling noise and pops.</summary>
    public bool LowCutEnabled { get; set; }
    public double LowCutHz { get; set; } = 80;
    public int LowCutSlope { get; set; } = 12;

    /// <summary>EQ on the mic only (after the low cut, before the dynamics and the reverb).</summary>
    public EqSettings MicEq { get; set; } = new();

    /// <summary>Compressor on the mic (after its EQ): evens out loud and soft speech.</summary>
    public bool CompressorEnabled { get; set; }
    public double CompressorThresholdDb { get; set; } = -24;
    public double CompressorRatio { get; set; } = 3;
    public double CompressorAttackMs { get; set; } = 10;
    public double CompressorReleaseMs { get; set; } = 150;
    public double CompressorKneeDb { get; set; } = 6;
    public double CompressorMakeupDb { get; set; }

    /// <summary>De-esser after the compressor: turns down the sibilance band when it gets loud.</summary>
    public bool DeEsserEnabled { get; set; }
    public double DeEsserFrequencyHz { get; set; } = 6000;
    public double DeEsserThresholdDb { get; set; } = -30;
    public double DeEsserRangeDb { get; set; } = 8;

    /// <summary>Peak limiter at the end of the mic chain (after the reverb).</summary>
    public bool LimiterEnabled { get; set; }
    public double LimiterCeilingDb { get; set; } = -1;
    public double LimiterReleaseMs { get; set; } = 80;

    /// <summary>EQ on everything sent to the HomePod.</summary>
    public EqSettings OutputEq { get; set; } = new();
}

public sealed class EqSettings
{
    /// <summary>"flat", "bass-boost", "vocal-boost", "reduce-bass" or "custom".</summary>
    public string Preset { get; set; } = "flat";

    /// <summary>
    /// Five-band values in dB at 60 / 250 / 1k / 3.5k / 12k Hz: the custom curve when Preset is "custom" and there are no
    /// <see cref="Bands"/> (as saved before 1.3). With bands, the curve's values there, so that an older version plays
    /// something close.
    /// </summary>
    public double[] Gains { get; set; } = new double[Equalizer.BandCount];

    /// <summary>Parametric bands, used when Preset is "custom"; null = the five-band <see cref="Gains"/>.</summary>
    public List<EqBandSettings>? Bands { get; set; }

    /// <summary>Gain after the bands (dB), e.g. to make room for boosts.</summary>
    public double OutputGainDb { get; set; }

    /// <summary>Off passes the sound unchanged (and keeps the settings for later).</summary>
    public bool Enabled { get; set; } = true;

    public EqSettings Clone() => new()
    {
        Preset = Preset,
        Gains = (double[]?)Gains?.Clone() ?? new double[Equalizer.BandCount],
        Bands = Bands?.Where(b => b != null).Select(b => b.Clone()).ToList(),
        OutputGainDb = OutputGainDb,
        Enabled = Enabled,
    };
}

/// <summary>One saved parametric band (see <see cref="EqBand"/>).</summary>
public sealed class EqBandSettings
{
    public EqFilterType Type { get; set; } = EqFilterType.Peak;
    public double Frequency { get; set; } = 1000;
    public double GainDb { get; set; }
    public double Q { get; set; } = 1;
    public int Slope { get; set; } = 12;
    public bool Enabled { get; set; } = true;

    public EqBand ToBand() => new EqBand(Type, Frequency, GainDb, Q, Slope, Enabled).Normalized();

    public static EqBandSettings From(EqBand band) => new()
    {
        Type = band.Type, Frequency = band.Frequency, GainDb = band.GainDb, Q = band.Q, Slope = band.Slope, Enabled = band.Enabled,
    };

    public EqBandSettings Clone() => (EqBandSettings)MemberwiseClone();
}

/// <summary>
/// The microphone and effects chain as one unit for the app to own (create once, keep for the app's
/// lifetime): mic → gain / noise gate → <see cref="LowCut"/> → <see cref="MicEq"/> → <see cref="Compressor"/> →
/// <see cref="DeEsser"/> → <see cref="Reverb"/> → <see cref="Limiter"/> → <see cref="HomePodSource"/>
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
    private volatile string? _capturedEndpoint;
    private string? _defaultOutput;

    public EffectsSettings Settings { get; }
    public Equalizer LowCut { get; } = new(MicCapture.OutputRate);
    public Equalizer MicEq { get; } = new(MicCapture.OutputRate);
    public DynamicsCompressor Compressor { get; } = new(MicCapture.OutputRate);
    public DeEsser DeEsser { get; } = new(MicCapture.OutputRate);
    public Reverb Reverb { get; } = new(MicCapture.OutputRate);
    public PeakLimiter Limiter { get; } = new(MicCapture.OutputRate);

    /// <summary>The voice chain after the gate, in order (what the mic runs on every packet).</summary>
    internal IAudioEffect[] Chain => [LowCut, MicEq, Compressor, DeEsser, Reverb, Limiter];

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

    /// <summary>
    /// 本机监听 is wanted but paused: its output device is the one the stream captures, so the voice would reach the
    /// speaker through the capture as well (twice, or although only 本机监听 was chosen). See <see cref="SetCaptured"/>.
    /// </summary>
    public bool MonitorBlocked { get; private set; }

    /// <summary>
    /// The render endpoint the stream captures now (null = nothing is captured) and the current default output (for
    /// a monitor that follows it). Called again whenever either changes; the monitor stops or starts accordingly.
    /// </summary>
    public void SetCaptured(string? capturedEndpointId, string? defaultOutputId)
    {
        lock (_lock)
        {
            if (CaptureEndpoint.SameChoice(_capturedEndpoint, capturedEndpointId) &&
                CaptureEndpoint.SameChoice(_defaultOutput, defaultOutputId))
                return;
            _capturedEndpoint = capturedEndpointId;
            _defaultOutput = defaultOutputId;
        }
        Apply();
    }

    /// <summary>Raised from audio threads when the mic or the monitor opens, fails or closes.</summary>
    public event Action? Changed;
    internal event Action? OutputSettingsChanged;

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
            LowCut.SetBands([new EqBand(EqFilterType.LowCut, s.LowCutHz, Slope: s.LowCutSlope, Enabled: s.LowCutEnabled)]);
            MicEq.Set(s.MicEq);
            if (_outputPresetOverride != null) OutputEq.SetPreset(_outputPresetOverride);
            else OutputEq.Set(s.OutputEq);
            OutputSettingsChanged?.Invoke();
            Compressor.ThresholdDb = (float)s.CompressorThresholdDb;
            Compressor.Ratio = (float)s.CompressorRatio;
            Compressor.AttackMs = (float)s.CompressorAttackMs;
            Compressor.ReleaseMs = (float)s.CompressorReleaseMs;
            Compressor.KneeDb = (float)s.CompressorKneeDb;
            Compressor.MakeupDb = (float)s.CompressorMakeupDb;
            Compressor.Enabled = s.CompressorEnabled;
            DeEsser.FrequencyHz = (float)s.DeEsserFrequencyHz;
            DeEsser.ThresholdDb = (float)s.DeEsserThresholdDb;
            DeEsser.RangeDb = (float)s.DeEsserRangeDb;
            DeEsser.Enabled = s.DeEsserEnabled;
            Reverb.Enabled = s.ReverbEnabled;
            Reverb.RoomSize = (float)s.ReverbRoomSize;
            Reverb.Damping = (float)s.ReverbDamping;
            Reverb.Mix = (float)s.ReverbMix;
            Reverb.PreDelayMs = (float)s.ReverbPreDelayMs;
            Limiter.CeilingDb = (float)s.LimiterCeilingDb;
            Limiter.ReleaseMs = (float)s.LimiterReleaseMs;
            Limiter.Enabled = s.LimiterEnabled;

            if (!_micOn)
            {
                SetBlocked(false);
                StopMonitor();
                StopMic();
                return;
            }

            if (_mic == null || _mic.DeviceId != Id(s.MicDeviceId))
            {
                StopMonitor();
                StopMic();
                _mic = new MicCapture(s.MicDeviceId) { Effects = Chain };
                _mic.StatusChanged += RaiseChanged;
                _mic.Start();
            }
            _mic.GainDb = (float)s.MicGainDb;
            _mic.GateEnabled = s.GateEnabled;
            _mic.GateThresholdDb = (float)s.GateThresholdDb;

            if (s.Destination != MicDestination.Monitor) _mic.Attach(HomePodSource);
            else _mic.Detach(HomePodSource);

            bool wanted = s.Destination != MicDestination.HomePod;
            bool blocked = wanted && CaptureEndpoint.MonitorCollides(s.MonitorDeviceId, _capturedEndpoint, _defaultOutput);
            SetBlocked(blocked);
            if (wanted && !blocked)
            {
                if (_monitor == null || _monitor.DeviceId != Id(s.MonitorDeviceId))
                {
                    StopMonitor();
                    _monitor = new LocalMonitor(_mic, s.MonitorDeviceId) { Refuse = id => CaptureEndpoint.Same(id, _capturedEndpoint) };
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

    private void SetBlocked(bool blocked)
    {
        if (MonitorBlocked == blocked) return;
        MonitorBlocked = blocked;
        if (blocked) Log.Info("monitor paused: its output device is the one being captured for the speaker");
        else Log.Info("monitor device is not captured (any more)");
        RaiseChanged();
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
