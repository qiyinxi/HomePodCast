using System.Text.Json.Serialization;

namespace HomePodCast.Audio;

public enum EqPreset { Flat, BassBoost, VocalBoost, ReduceBass, Custom }

/// <summary>
/// EQ presets. Each has a stable id for config files and for code that switches presets on its own
/// (e.g. a night mode turning on "reduce-bass" together with the compressor).
/// </summary>
public static class EqPresets
{
    public static IReadOnlyList<EqPreset> All { get; } =
        [EqPreset.Flat, EqPreset.BassBoost, EqPreset.VocalBoost, EqPreset.ReduceBass, EqPreset.Custom];

    public static string Id(EqPreset preset) => preset switch
    {
        EqPreset.BassBoost => "bass-boost",
        EqPreset.VocalBoost => "vocal-boost",
        EqPreset.ReduceBass => "reduce-bass",
        EqPreset.Custom => "custom",
        _ => "flat",
    };

    public static EqPreset FromId(string? id) => id switch
    {
        "bass-boost" => EqPreset.BassBoost,
        "vocal-boost" => EqPreset.VocalBoost,
        "reduce-bass" => EqPreset.ReduceBass,
        "custom" => EqPreset.Custom,
        _ => EqPreset.Flat,
    };

    public static string Name(EqPreset preset) => preset switch
    {
        EqPreset.BassBoost => L.T("低音增强"),
        EqPreset.VocalBoost => L.T("人声增强"),
        EqPreset.ReduceBass => L.T("减弱低音"),
        EqPreset.Custom => L.T("自定义"),
        _ => L.T("平直"),
    };

    /// <summary>Band gains in dB at <see cref="Equalizer.BandFrequencies"/>; null for Custom.</summary>
    public static double[]? Gains(EqPreset preset) => preset switch
    {
        EqPreset.Flat => [0, 0, 0, 0, 0],
        EqPreset.BassBoost => [6, 2, 0, 0, 0],
        EqPreset.VocalBoost => [-3, -1, 2, 4, 1],
        // Like the HomePod's own "Reduce Bass" (Home app), which AirPlay cannot switch: a gentle low shelf,
        // about −8 dB in the deep bass, −4 dB around 130 Hz, back to 0 by ~400 Hz.
        EqPreset.ReduceBass => [-8, -1, 0, 0, 0],
        _ => null,
    };
}

/// <summary>Filter shapes of a parametric EQ band (RBJ cookbook biquads; the cuts cascade Butterworth sections).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EqFilterType>))]
public enum EqFilterType { Peak, LowShelf, HighShelf, LowCut, HighCut, Notch }

/// <summary>
/// One band of a parametric EQ. <see cref="Q"/> is the bandwidth of a peak or notch, the knee of a shelf and the resonance
/// of a 12 dB/oct cut (steeper cuts are Butterworth: flat, no resonance). <see cref="Slope"/> (dB/octave) only for cuts.
/// </summary>
public readonly record struct EqBand(EqFilterType Type, double Frequency, double GainDb = 0, double Q = 0.707,
    int Slope = 12, bool Enabled = true)
{
    public bool HasGain => Type is EqFilterType.Peak or EqFilterType.LowShelf or EqFilterType.HighShelf;
    public bool IsCut => Type is EqFilterType.LowCut or EqFilterType.HighCut;

    /// <summary>Q changes the shape: not for cuts steeper than 12 dB/oct.</summary>
    public bool HasQ => !IsCut || Slope <= 12;

    /// <summary>Biquad sections the band needs: one, or one per 12 dB/oct of a cut.</summary>
    public int Sections => IsCut ? Math.Clamp(Slope / 12, 1, Equalizer.MaxSections) : 1;

    /// <summary>In the ranges the engine and the editor support (finite, frequency inside 20 Hz–20 kHz, …).</summary>
    public EqBand Normalized() => this with
    {
        Type = Enum.IsDefined(Type) ? Type : EqFilterType.Peak,
        Frequency = double.IsFinite(Frequency) ? Math.Clamp(Frequency, Equalizer.MinFrequency, Equalizer.MaxFrequency) : 1000,
        GainDb = double.IsFinite(GainDb) ? Math.Clamp(GainDb, -Equalizer.MaxGainDb, Equalizer.MaxGainDb) : 0,
        Q = double.IsFinite(Q) ? Math.Clamp(Q, Equalizer.MinQ, Equalizer.MaxQ) : 0.707,
        Slope = Math.Clamp((int)Math.Round(Slope / 12.0) * 12, 12, 12 * Equalizer.MaxSections),
    };
}

/// <summary>
/// Stereo parametric equalizer: up to <see cref="MaxBands"/> bands of <see cref="EqFilterType"/> plus an output gain.
/// <para>
/// The five-band presets (and the "custom" curves saved before the parametric editor) keep their own rule: a low shelf,
/// three peaks and a high shelf whose gains are solved (interaction matrix) so the curve passes through the five slider
/// values at <see cref="BandFrequencies"/>. They load as five ordinary bands, so they sound exactly as before.
/// </para>
/// <para>
/// Parameters are published as one immutable target from any thread; the audio thread glides gain, frequency (in
/// octaves) and Q (~30 ms) with per-sample coefficient ramps, and crossfades a band whose shape changes (type, slope,
/// on/off, added or removed), so no edit clicks. Flat settles into a bit-exact bypass. Process is allocation-free.
/// </para>
/// </summary>
public sealed class Equalizer : IAudioEffect
{
    /// <summary>Bands of the five-band presets (<see cref="EqPresets"/>, <see cref="EqSettings.Gains"/>).</summary>
    public const int BandCount = 5;
    public const int MaxBands = 12;
    public const int MaxSections = 4;
    public const double MinFrequency = 20, MaxFrequency = 20000, MaxGainDb = 24, MinQ = 0.1, MaxQ = 20;
    public const double MinOutputGainDb = -24, MaxOutputGainDb = 12;

    private const int Block = 16;               // coefficient update interval (frames)
    private const double MaxInternalDb = 24;
    private const double SmoothingSeconds = 0.03;
    private const double CrossfadeSeconds = 0.02;

    // The five-band presets: filter (type, corner/centre, Q) and the frequency where the slider value is met. The
    // shelves are controlled inside their plateau so "bass −8 dB" means the deep bass is 8 dB down.
    private static readonly (EqFilterType Type, double F0, double Q, double Control)[] Legacy =
    [
        (EqFilterType.LowShelf, 140, 0.6, 60),
        (EqFilterType.Peak, 250, 1.0, 250),
        (EqFilterType.Peak, 1000, 1.0, 1000),
        (EqFilterType.Peak, 3500, 1.0, 3500),
        (EqFilterType.HighShelf, 6000, 0.6, 12000),
    ];

    /// <summary>Butterworth section Qs for cuts of 12, 24, 36 and 48 dB/oct.</summary>
    private static readonly double[][] Butterworth =
    [
        [0.70710678],
        [0.54119610, 1.30656296],
        [0.51763809, 0.70710678, 1.93185165],
        [0.50979558, 0.60134489, 0.89997622, 2.56291545],
    ];

    public static IReadOnlyList<double> BandFrequencies { get; } = Legacy.Select(b => b.Control).ToArray();
    public static IReadOnlyList<string> BandLabels { get; } = ["60", "250", "1k", "3.5k", "12k"];

    /// <summary>Everything the audio thread works towards; replaced as a whole, never changed.</summary>
    private sealed record Target(EqBand[] Bands, double OutputGainDb);

    private struct Section
    {
        public double B0, B1, B2, A1, A2;       // current
        public double NB0, NB1, NB2, NA1, NA2;  // at the end of the block being ramped
        public double ZL1, ZL2, ZR1, ZR2;
        public readonly bool Silent => ZL1 == 0 && ZL2 == 0 && ZR1 == 0 && ZR2 == 0;
    }

    /// <summary>A band position on the audio thread: the filter it runs now, and how far it is mixed in.</summary>
    private struct Slot
    {
        public bool Present;
        public EqFilterType Type;
        public int Sections;
        public double Gain, LogF, LogQ;          // current parameters (log2 Hz, log2 Q)
        public double Mix, MixFrom;              // 1 = the filter, 0 = bypassed
        public bool Ramping, Mixing;
    }

    private readonly int _rate;
    private readonly double _smooth, _mixStep, _nyquistLimit;
    private readonly Slot[] _slots = new Slot[MaxBands];
    private readonly Section[] _sections = new Section[MaxBands * MaxSections];
    private readonly float[] _dry = new float[Block * 2];
    private volatile Target _target = new([], 0);
    private double _outGain = 1, _outFrom = 1;
    private int _identityFrames;
    private bool _started;
    private volatile bool _resetRequested;

    private double[] _gains = new double[BandCount];

    public int SampleRate => _rate;
    public EqPreset Preset { get; private set; } = EqPreset.Flat;

    /// <summary>
    /// The five-band values (dB at <see cref="BandFrequencies"/>) of the current settings: a preset's or saved custom
    /// slider values, or for parametric bands the curve's response there.
    /// </summary>
    public double[] GainsDb => (double[])_gains.Clone();

    /// <summary>The bands the audio thread works towards (a five-band preset as its five filters).</summary>
    public IReadOnlyList<EqBand> Bands => _target.Bands;

    public double OutputGainDb => _target.OutputGainDb;

    /// <summary>The five-band filter gains actually used after overlap compensation (dB).</summary>
    internal double[] InternalGainsDb => _target.Bands.Length == BandCount ? _target.Bands.Select(b => b.GainDb).ToArray() : new double[BandCount];

    public Equalizer(int sampleRate = 44100)
    {
        _rate = sampleRate;
        _smooth = 1 - Math.Exp(-Block / (SmoothingSeconds * sampleRate));
        _mixStep = Block / (CrossfadeSeconds * sampleRate);
        _nyquistLimit = sampleRate * 0.45;
        Publish(LegacyBands(EqPresets.Gains(EqPreset.Flat)!, sampleRate), 0);
    }

    // ---------------------------------------------------------------- settings (any thread)

    public void SetPreset(string id) => SetPreset(EqPresets.FromId(id));

    /// <summary>Switch to a built-in preset (Custom keeps the current settings).</summary>
    public void SetPreset(EqPreset preset)
    {
        if (EqPresets.Gains(preset) is { } gains) SetLegacy(gains, 0);
        Preset = preset;
    }

    /// <summary>Set custom five-band gains in dB (switches the preset to Custom).</summary>
    public void SetGains(ReadOnlySpan<double> gainsDb)
    {
        SetLegacy(gainsDb, 0);
        Preset = EqPreset.Custom;
    }

    /// <summary>Preset by id, or the given custom five-band gains when the id is "custom".</summary>
    public void Set(string? presetId, ReadOnlySpan<double> customGains)
    {
        var preset = EqPresets.FromId(presetId);
        if (preset == EqPreset.Custom) SetGains(customGains);
        else SetPreset(preset);
    }

    /// <summary>
    /// Saved settings: a five-band preset, custom five-band gains, or parametric <see cref="EqSettings.Bands"/>; with the
    /// output gain, and all of it bypassed (gliding) when <see cref="EqSettings.Enabled"/> is off.
    /// </summary>
    public void Set(EqSettings? settings)
    {
        settings ??= new EqSettings();
        var preset = EqPresets.FromId(settings.Preset);
        double output = settings.Enabled && double.IsFinite(settings.OutputGainDb)
            ? Math.Clamp(settings.OutputGainDb, MinOutputGainDb, MaxOutputGainDb) : 0;
        if (preset == EqPreset.Custom && settings.Bands is { Count: > 0 } saved)
        {
            var bands = saved.Where(b => b != null).Take(MaxBands).Select(b => b.ToBand()).ToArray();
            if (!settings.Enabled) bands = bands.Select(b => b with { Enabled = false }).ToArray();
            Publish(bands, output);
            _gains = LegacyValues(bands, output, _rate);
        }
        else
        {
            var gains = EqPresets.Gains(preset) ?? settings.Gains ?? [];
            SetLegacy(settings.Enabled ? gains : new double[BandCount], output);
        }
        Preset = preset;
    }

    /// <summary>Parametric bands (at most <see cref="MaxBands"/>) and the output gain; the preset becomes Custom.</summary>
    public void SetBands(IReadOnlyList<EqBand> bands, double outputGainDb = 0)
    {
        var normalized = bands.Take(MaxBands).Select(b => b.Normalized()).ToArray();
        double output = double.IsFinite(outputGainDb) ? Math.Clamp(outputGainDb, MinOutputGainDb, MaxOutputGainDb) : 0;
        Publish(normalized, output);
        _gains = LegacyValues(normalized, output, _rate);
        Preset = EqPreset.Custom;
    }

    private void SetLegacy(ReadOnlySpan<double> gainsDb, double outputGainDb)
    {
        var want = new double[BandCount];
        for (int i = 0; i < BandCount; i++)
            want[i] = i < gainsDb.Length && double.IsFinite(gainsDb[i]) ? Math.Clamp(gainsDb[i], -18, 18) : 0;
        _gains = want;
        Publish(LegacyBands(want, _rate), outputGainDb);
    }

    private void Publish(EqBand[] bands, double outputGainDb) => _target = new Target(bands, outputGainDb);

    // ---------------------------------------------------------------- the five-band presets as parametric bands

    private static readonly Dictionary<int, double[,]> Inverses = new();

    /// <summary>
    /// The five filters of a five-band curve: a low shelf, three peaks and a high shelf whose gains are solved so that the
    /// response at <see cref="BandFrequencies"/> is <paramref name="gainsDb"/> (within a few hundredths of a dB).
    /// </summary>
    public static EqBand[] LegacyBands(ReadOnlySpan<double> gainsDb, int sampleRate = 44100)
    {
        var inverse = Inverse(sampleRate);
        Span<double> want = stackalloc double[BandCount];
        for (int i = 0; i < BandCount; i++)
            want[i] = i < gainsDb.Length && double.IsFinite(gainsDb[i]) ? Math.Clamp(gainsDb[i], -18, 18) : 0;

        // Linear first guess, then a few Newton-like corrections with the fixed interaction matrix: the dB response is
        // only approximately linear in the gain, so this lands within a few hundredths of a dB.
        Span<double> g = stackalloc double[BandCount];
        Span<double> err = stackalloc double[BandCount];
        Span<double> step = stackalloc double[BandCount];
        MultiplyInverse(inverse, want, g);
        for (int iter = 0; iter < 6; iter++)
        {
            for (int i = 0; i < BandCount; i++)
            {
                double r = 0;
                for (int j = 0; j < BandCount; j++) r += ResponseDb(LegacyBand(j, g[j]), Legacy[i].Control, sampleRate);
                err[i] = want[i] - r;
            }
            MultiplyInverse(inverse, err, step);
            for (int j = 0; j < BandCount; j++) g[j] = Math.Clamp(g[j] + step[j], -MaxInternalDb, MaxInternalDb);
        }
        var bands = new EqBand[BandCount];
        for (int j = 0; j < BandCount; j++) bands[j] = LegacyBand(j, Math.Abs(g[j]) < 1e-6 ? 0 : g[j]);
        return bands;
    }

    private static EqBand LegacyBand(int j, double gainDb) => new(Legacy[j].Type, Legacy[j].F0, gainDb, Legacy[j].Q);

    /// <summary>The response of <paramref name="bands"/> at the five-band frequencies, rounded to 0.1 dB (for older versions).</summary>
    public static double[] LegacyValues(IReadOnlyList<EqBand> bands, double outputGainDb = 0, int sampleRate = 44100) =>
        Legacy.Select(b => Math.Round(Math.Clamp(ResponseDb(bands, b.Control, sampleRate) + outputGainDb, -18, 18), 1)).ToArray();

    private static double[,] Inverse(int sampleRate)
    {
        lock (Inverses)
        {
            if (Inverses.TryGetValue(sampleRate, out var known)) return known;
            // Interaction matrix: response (dB) of band j at band i's frequency, per dB of band j's gain.
            const double probe = 6;
            var m = new double[BandCount, BandCount];
            for (int i = 0; i < BandCount; i++)
                for (int j = 0; j < BandCount; j++)
                    m[i, j] = ResponseDb(LegacyBand(j, probe), Legacy[i].Control, sampleRate) / probe;
            return Inverses[sampleRate] = Invert(m);
        }
    }

    private static void MultiplyInverse(double[,] inverse, ReadOnlySpan<double> v, Span<double> result)
    {
        for (int i = 0; i < BandCount; i++)
        {
            double s = 0;
            for (int j = 0; j < BandCount; j++) s += inverse[i, j] * v[j];
            result[i] = s;
        }
    }

    // ---------------------------------------------------------------- response

    /// <summary>Magnitude response in dB at <paramref name="frequency"/> for the current settings (not the glide).</summary>
    public double MagnitudeDb(double frequency)
    {
        var t = _target;
        return ResponseDb(t.Bands, frequency, _rate) + t.OutputGainDb;
    }

    /// <summary>Largest boost anywhere in 20 Hz–20 kHz (dB, ≥ 0): the headroom a mixer needs to avoid clipping.</summary>
    public double PeakGainDb
    {
        get
        {
            double peak = 0;
            for (double f = 20; f <= Math.Min(20000, _rate * 0.45); f *= 1.02) peak = Math.Max(peak, MagnitudeDb(f));
            return peak;
        }
    }

    /// <summary>Response (dB) of a set of bands, without output gain.</summary>
    public static double ResponseDb(IReadOnlyList<EqBand> bands, double frequency, int sampleRate = 44100)
    {
        double sum = 0;
        for (int i = 0; i < bands.Count; i++) sum += ResponseDb(bands[i], frequency, sampleRate);
        return sum;
    }

    /// <summary>Response (dB) of one band at <paramref name="frequency"/>; 0 when it is off.</summary>
    public static double ResponseDb(EqBand band, double frequency, int sampleRate = 44100)
    {
        if (!band.Enabled || band.HasGain && band.GainDb == 0) return 0;
        double limit = sampleRate * 0.45;
        double f0 = Math.Clamp(band.Frequency, MinFrequency, limit);
        double w = 2 * Math.PI * Math.Min(frequency, sampleRate * 0.4999) / sampleRate;
        double c1 = Math.Cos(w), s1 = Math.Sin(w), c2 = Math.Cos(2 * w), s2 = Math.Sin(2 * w);
        double db = 0;
        for (int k = 0; k < band.Sections; k++)
        {
            Coefficients(band.Type, band.GainDb, f0, SectionQ(band.Type, band.Q, band.Sections, k), sampleRate,
                out var b0, out var b1, out var b2, out var a1, out var a2);
            double nr = b0 + b1 * c1 + b2 * c2, ni = -(b1 * s1 + b2 * s2);
            double dr = 1 + a1 * c1 + a2 * c2, di = -(a1 * s1 + a2 * s2);
            db += 10 * Math.Log10(Math.Max(1e-30, nr * nr + ni * ni) / Math.Max(1e-30, dr * dr + di * di));
        }
        return db;
    }

    private static double SectionQ(EqFilterType type, double q, int sections, int k) =>
        type is EqFilterType.LowCut or EqFilterType.HighCut && sections > 1 ? Butterworth[sections - 1][k] : q;

    /// <summary>RBJ cookbook biquad, normalized so a0 = 1.</summary>
    private static void Coefficients(EqFilterType type, double gainDb, double f0, double q, int rate,
        out double b0, out double b1, out double b2, out double a1, out double a2)
    {
        double w0 = 2 * Math.PI * f0 / rate;
        double cos = Math.Cos(w0), sin = Math.Sin(w0);
        double alpha = sin / (2 * q);
        double a = Math.Pow(10, gainDb / 40);
        double a0;
        switch (type)
        {
            case EqFilterType.Peak:
                b0 = 1 + alpha * a;
                b1 = -2 * cos;
                b2 = 1 - alpha * a;
                a0 = 1 + alpha / a;
                a1 = -2 * cos;
                a2 = 1 - alpha / a;
                break;
            case EqFilterType.LowShelf:
            {
                double s = 2 * Math.Sqrt(a) * alpha;
                b0 = a * ((a + 1) - (a - 1) * cos + s);
                b1 = 2 * a * ((a - 1) - (a + 1) * cos);
                b2 = a * ((a + 1) - (a - 1) * cos - s);
                a0 = (a + 1) + (a - 1) * cos + s;
                a1 = -2 * ((a - 1) + (a + 1) * cos);
                a2 = (a + 1) + (a - 1) * cos - s;
                break;
            }
            case EqFilterType.HighShelf:
            {
                double s = 2 * Math.Sqrt(a) * alpha;
                b0 = a * ((a + 1) + (a - 1) * cos + s);
                b1 = -2 * a * ((a - 1) + (a + 1) * cos);
                b2 = a * ((a + 1) + (a - 1) * cos - s);
                a0 = (a + 1) - (a - 1) * cos + s;
                a1 = 2 * ((a - 1) - (a + 1) * cos);
                a2 = (a + 1) - (a - 1) * cos - s;
                break;
            }
            case EqFilterType.LowCut:
                b0 = (1 + cos) / 2;
                b1 = -(1 + cos);
                b2 = (1 + cos) / 2;
                a0 = 1 + alpha;
                a1 = -2 * cos;
                a2 = 1 - alpha;
                break;
            case EqFilterType.HighCut:
                b0 = (1 - cos) / 2;
                b1 = 1 - cos;
                b2 = (1 - cos) / 2;
                a0 = 1 + alpha;
                a1 = -2 * cos;
                a2 = 1 - alpha;
                break;
            default: // notch
                b0 = 1;
                b1 = -2 * cos;
                b2 = 1;
                a0 = 1 + alpha;
                a1 = -2 * cos;
                a2 = 1 - alpha;
                break;
        }
        b0 /= a0; b1 /= a0; b2 /= a0; a1 /= a0; a2 /= a0;
    }

    // ---------------------------------------------------------------- audio thread

    /// <summary>Clears the filter state (on the audio thread, at the next Process).</summary>
    public void Reset() => _resetRequested = true;

    public void Process(Span<float> interleaved, int frames)
    {
        var target = _target;
        if (_resetRequested)
        {
            _resetRequested = false;
            ClearState();
        }
        if (!_started)
        {
            // The first block starts at the configured curve instead of gliding from flat.
            _started = true;
            for (int i = 0; i < MaxBands; i++)
            {
                if (i < target.Bands.Length && target.Bands[i].Enabled) Start(i, target.Bands[i], mix: 1);
            }
            _outGain = _outFrom = FromDb(target.OutputGainDb);
        }

        for (int pos = 0; pos < frames; pos += Block)
        {
            int n = Math.Min(Block, frames - pos);
            if (Glide(target))
            {
                // Flat: let the filter memory ring out, then bypass (bit-exact) with cleared state.
                if (_identityFrames > 4096)
                {
                    Commit();
                    continue;
                }
                _identityFrames += n;
                if (_identityFrames > 4096)
                {
                    ClearState();
                    Commit();
                    continue;
                }
            }
            else
            {
                _identityFrames = 0;
            }
            var block = interleaved.Slice(pos * 2, n * 2);
            for (int i = 0; i < MaxBands; i++) RunSlot(i, block, n);
            ApplyOutputGain(block, n);
            Commit();
        }
    }

    private static double FromDb(double db) => Math.Pow(10, db / 20);

    private void ClearState()
    {
        for (int s = 0; s < _sections.Length; s++)
        {
            ref var sec = ref _sections[s];
            sec.ZL1 = sec.ZL2 = sec.ZR1 = sec.ZR2 = 0;
        }
    }

    /// <summary>Begin running <paramref name="band"/> in slot <paramref name="i"/> at its parameters, mixed in by <paramref name="mix"/>.</summary>
    private void Start(int i, EqBand band, double mix)
    {
        ref var slot = ref _slots[i];
        slot.Present = true;
        slot.Type = band.Type;
        slot.Sections = band.Sections;
        slot.Gain = band.GainDb;
        slot.LogF = Math.Log2(Math.Clamp(band.Frequency, MinFrequency, _nyquistLimit));
        slot.LogQ = Math.Log2(band.Q);
        slot.Mix = slot.MixFrom = mix;
        slot.Ramping = slot.Mixing = false;
        for (int k = 0; k < MaxSections; k++)
        {
            ref var sec = ref _sections[i * MaxSections + k];
            sec.ZL1 = sec.ZL2 = sec.ZR1 = sec.ZR2 = 0;
            if (k >= slot.Sections) continue;
            SlotCoefficients(ref slot, k, out sec.B0, out sec.B1, out sec.B2, out sec.A1, out sec.A2);
        }
    }

    private void SlotCoefficients(ref Slot slot, int k, out double b0, out double b1, out double b2, out double a1, out double a2) =>
        Coefficients(slot.Type, slot.Gain, Math.Pow(2, slot.LogF), SectionQ(slot.Type, Math.Pow(2, slot.LogQ), slot.Sections, k), _rate,
            out b0, out b1, out b2, out a1, out a2);

    /// <summary>
    /// Move every slot one block towards the target. A slot whose parameters move gets block-end coefficients, ramped to
    /// sample by sample; one whose filter shape changes fades out, switches, and fades in. True when nothing filters.
    /// </summary>
    private bool Glide(Target target)
    {
        bool identity = true;
        for (int i = 0; i < MaxBands; i++)
        {
            ref var slot = ref _slots[i];
            bool wanted = i < target.Bands.Length && target.Bands[i].Enabled;
            var band = wanted ? target.Bands[i] : default;
            if (!slot.Present)
            {
                if (!wanted) continue;
                Start(i, band, mix: 0);
            }
            bool same = wanted && band.Type == slot.Type && band.Sections == slot.Sections;
            if (same)
            {
                double logF = Math.Log2(Math.Clamp(band.Frequency, MinFrequency, _nyquistLimit)), logQ = Math.Log2(band.Q);
                bool moved = false;
                moved |= Approach(ref slot.Gain, band.GainDb, 0.002);
                moved |= Approach(ref slot.LogF, logF, 0.0002);
                moved |= Approach(ref slot.LogQ, logQ, 0.0002);
                slot.Ramping = moved;
                if (moved)
                {
                    for (int k = 0; k < slot.Sections; k++)
                    {
                        ref var sec = ref _sections[i * MaxSections + k];
                        SlotCoefficients(ref slot, k, out sec.NB0, out sec.NB1, out sec.NB2, out sec.NA1, out sec.NA2);
                    }
                }
            }
            double mixTarget = same ? 1 : 0;
            slot.MixFrom = slot.Mix;
            slot.Mixing = slot.Mix != mixTarget;
            if (slot.Mixing)
                slot.Mix = mixTarget > slot.Mix ? Math.Min(1, slot.Mix + _mixStep) : Math.Max(0, slot.Mix - _mixStep);

            bool flat = !slot.Mixing && (slot.Mix == 0 || slot.Type is EqFilterType.Peak or EqFilterType.LowShelf or EqFilterType.HighShelf
                && slot.Gain == 0 && !slot.Ramping);
            if (!flat) identity = false;
        }
        double outTarget = FromDb(target.OutputGainDb);
        _outFrom = _outGain;
        if (_outGain != outTarget)
        {
            _outGain += (outTarget - _outGain) * _smooth;
            if (Math.Abs(outTarget - _outGain) < 1e-5) _outGain = outTarget;
        }
        if (_outGain != 1 || _outFrom != 1) identity = false;
        return identity;
    }

    private bool Approach(ref double current, double target, double snap)
    {
        if (current == target) return false;
        current += (target - current) * _smooth;
        if (Math.Abs(target - current) < snap) current = target;
        return true;
    }

    /// <summary>Make the block-end values current (also when the block was bypassed); retire slots that faded out.</summary>
    private void Commit()
    {
        for (int i = 0; i < MaxBands; i++)
        {
            ref var slot = ref _slots[i];
            if (!slot.Present) continue;
            if (slot.Ramping)
            {
                slot.Ramping = false;
                for (int k = 0; k < slot.Sections; k++)
                {
                    ref var sec = ref _sections[i * MaxSections + k];
                    (sec.B0, sec.B1, sec.B2, sec.A1, sec.A2) = (sec.NB0, sec.NB1, sec.NB2, sec.NA1, sec.NA2);
                }
            }
            slot.MixFrom = slot.Mix;
            slot.Mixing = false;
            if (slot.Mix == 0) slot.Present = false; // a new or changed band starts afresh (Glide → Start)
        }
        _outFrom = _outGain;
    }

    private void RunSlot(int i, Span<float> x, int frames)
    {
        ref var slot = ref _slots[i];
        if (!slot.Present) return;
        bool mixing = slot.Mixing || slot.Mix != 1;
        if (!mixing && !slot.Ramping && slot.Type is EqFilterType.Peak or EqFilterType.LowShelf or EqFilterType.HighShelf && slot.Gain == 0)
        {
            bool silent = true;
            for (int k = 0; k < slot.Sections; k++) silent &= _sections[i * MaxSections + k].Silent;
            if (silent) return; // flat and rung out
        }
        if (slot.MixFrom == 0 && slot.Mix == 0) return;
        if (mixing) x.CopyTo(_dry);
        for (int k = 0; k < slot.Sections; k++) Filter(ref _sections[i * MaxSections + k], slot.Ramping, x, frames);
        if (!mixing) return;
        double m = slot.MixFrom, dm = (slot.Mix - slot.MixFrom) / frames;
        for (int f = 0; f < frames; f++)
        {
            m += dm;
            // Smoothstep: a band that starts from rest jumps on its first sample (a high-pass passes the sine's value at
            // once); mixed in linearly, that step showed as a click (2026-10-27, peak → low cut).
            double w = m * m * (3 - 2 * m);
            x[f * 2] = (float)(_dry[f * 2] + w * (x[f * 2] - _dry[f * 2]));
            x[f * 2 + 1] = (float)(_dry[f * 2 + 1] + w * (x[f * 2 + 1] - _dry[f * 2 + 1]));
        }
    }

    private static void Filter(ref Section s, bool ramp, Span<float> x, int frames)
    {
        double b0 = s.B0, b1 = s.B1, b2 = s.B2, a1 = s.A1, a2 = s.A2;
        double db0 = 0, db1 = 0, db2 = 0, da1 = 0, da2 = 0;
        if (ramp)
        {
            double k = 1.0 / frames;
            db0 = (s.NB0 - b0) * k; db1 = (s.NB1 - b1) * k; db2 = (s.NB2 - b2) * k;
            da1 = (s.NA1 - a1) * k; da2 = (s.NA2 - a2) * k;
        }
        double l1 = s.ZL1, l2 = s.ZL2, r1 = s.ZR1, r2 = s.ZR2;
        for (int i = 0; i < frames; i++)
        {
            b0 += db0; b1 += db1; b2 += db2; a1 += da1; a2 += da2;
            // Transposed direct form II: well behaved when the coefficients change.
            double inL = x[i * 2], inR = x[i * 2 + 1];
            double outL = b0 * inL + l1;
            l1 = b1 * inL - a1 * outL + l2;
            l2 = b2 * inL - a2 * outL;
            double outR = b0 * inR + r1;
            r1 = b1 * inR - a1 * outR + r2;
            r2 = b2 * inR - a2 * outR;
            x[i * 2] = (float)outL;
            x[i * 2 + 1] = (float)outR;
        }
        // A NaN or infinite input sample (a driver, an APO, a virtual mic) would stay in the state for good and silence
        // everything after it: start over from rest instead.
        if (!double.IsFinite(l1 + l2 + r1 + r2)) l1 = l2 = r1 = r2 = 0;
        // Flush tiny state so silence never decays into (slow) subnormal arithmetic.
        s.ZL1 = Math.Abs(l1) < 1e-25 ? 0 : l1;
        s.ZL2 = Math.Abs(l2) < 1e-25 ? 0 : l2;
        s.ZR1 = Math.Abs(r1) < 1e-25 ? 0 : r1;
        s.ZR2 = Math.Abs(r2) < 1e-25 ? 0 : r2;
    }

    private void ApplyOutputGain(Span<float> x, int frames)
    {
        if (_outGain == 1 && _outFrom == 1) return;
        double g = _outFrom, dg = (_outGain - _outFrom) / frames;
        for (int f = 0; f < frames; f++)
        {
            g += dg;
            x[f * 2] = (float)(x[f * 2] * g);
            x[f * 2 + 1] = (float)(x[f * 2 + 1] * g);
        }
    }

    private static double[,] Invert(double[,] m)
    {
        int n = m.GetLength(0);
        var a = (double[,])m.Clone();
        var inv = new double[n, n];
        for (int i = 0; i < n; i++) inv[i, i] = 1;
        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < n; r++) if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col])) pivot = r;
            for (int c = 0; c < n; c++)
            {
                (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
                (inv[col, c], inv[pivot, c]) = (inv[pivot, c], inv[col, c]);
            }
            double d = a[col, col];
            for (int c = 0; c < n; c++) { a[col, c] /= d; inv[col, c] /= d; }
            for (int r = 0; r < n; r++)
            {
                if (r == col) continue;
                double f = a[r, col];
                if (f == 0) continue;
                for (int c = 0; c < n; c++) { a[r, c] -= f * a[col, c]; inv[r, c] -= f * inv[col, c]; }
            }
        }
        return inv;
    }
}
