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

/// <summary>
/// Five-band stereo equalizer from RBJ-cookbook biquads: a low shelf, three peaking bands and a high shelf.
/// The band gains are the response wanted *at the band frequencies*: the overlap between neighbouring
/// filters is compensated (interaction-matrix solve), so the curve goes through the slider values.
/// Gain changes glide (~30 ms) so preset switches never click; Process is allocation-free.
/// </summary>
public sealed class Equalizer : IStereoEffect
{
    public const int BandCount = 5;
    private const int Block = 16;               // coefficient update interval (frames)
    private const double MaxInternalDb = 24;
    private const double SmoothingSeconds = 0.03;

    private enum Kind { LowShelf, Peak, HighShelf }

    // Filter (type, corner/centre, Q) and the frequency where the slider value is met. The shelves are
    // controlled inside their plateau so "bass −8 dB" means the deep bass is 8 dB down.
    private static readonly (Kind Kind, double F0, double Q, double Control)[] Bands =
    [
        (Kind.LowShelf, 140, 0.6, 60),
        (Kind.Peak, 250, 1.0, 250),
        (Kind.Peak, 1000, 1.0, 1000),
        (Kind.Peak, 3500, 1.0, 3500),
        (Kind.HighShelf, 6000, 0.6, 12000),
    ];

    public static IReadOnlyList<double> BandFrequencies { get; } = Bands.Select(b => b.Control).ToArray();
    public static IReadOnlyList<string> BandLabels { get; } = ["60", "250", "1k", "3.5k", "12k"];

    private readonly int _rate;
    private readonly double[] _cos = new double[BandCount], _sin = new double[BandCount];
    private readonly double[,] _inverse;        // inverse interaction matrix (dB per dB)

    // Written by the UI thread, read by the audio thread (aligned doubles: atomic on x64).
    private readonly double[] _target = new double[BandCount];

    // Audio-thread state.
    private readonly double[] _current = new double[BandCount];
    private readonly double[] _b0 = new double[BandCount], _b1 = new double[BandCount], _b2 = new double[BandCount];
    private readonly double[] _a1 = new double[BandCount], _a2 = new double[BandCount];
    private readonly double[] _nb0 = new double[BandCount], _nb1 = new double[BandCount], _nb2 = new double[BandCount];
    private readonly double[] _na1 = new double[BandCount], _na2 = new double[BandCount];
    private readonly bool[] _ramping = new bool[BandCount];
    private readonly double[] _zl1 = new double[BandCount], _zl2 = new double[BandCount];
    private readonly double[] _zr1 = new double[BandCount], _zr2 = new double[BandCount];
    private readonly double _smooth;
    private int _identityFrames;
    private bool _started;
    private volatile bool _resetRequested;

    private readonly double[] _gains = new double[BandCount];

    public int SampleRate => _rate;
    public EqPreset Preset { get; private set; } = EqPreset.Flat;

    /// <summary>The band gains currently asked for (dB), i.e. the curve's values at the band frequencies.</summary>
    public double[] GainsDb => (double[])_gains.Clone();

    /// <summary>The filter gains actually used after overlap compensation (dB).</summary>
    internal double[] InternalGainsDb => (double[])_target.Clone();

    public Equalizer(int sampleRate = 44100)
    {
        _rate = sampleRate;
        for (int i = 0; i < BandCount; i++)
        {
            double w0 = 2 * Math.PI * Bands[i].F0 / sampleRate;
            _cos[i] = Math.Cos(w0);
            _sin[i] = Math.Sin(w0);
            Coefficients(i, 0, out _b0[i], out _b1[i], out _b2[i], out _a1[i], out _a2[i]);
        }
        _smooth = 1 - Math.Exp(-Block / (SmoothingSeconds * sampleRate));

        // Interaction matrix: response (dB) of band j at band i's frequency, per dB of band j's gain.
        const double probe = 6;
        var m = new double[BandCount, BandCount];
        for (int i = 0; i < BandCount; i++)
            for (int j = 0; j < BandCount; j++)
                m[i, j] = BandResponseDb(j, probe, Bands[i].Control) / probe;
        _inverse = Invert(m);
    }

    public void SetPreset(string id) => SetPreset(EqPresets.FromId(id));

    /// <summary>Switch to a built-in preset (Custom keeps the current gains).</summary>
    public void SetPreset(EqPreset preset)
    {
        if (EqPresets.Gains(preset) is { } gains) Apply(gains);
        Preset = preset;
    }

    /// <summary>Set custom band gains in dB (switches the preset to Custom).</summary>
    public void SetGains(ReadOnlySpan<double> gainsDb)
    {
        Apply(gainsDb);
        Preset = EqPreset.Custom;
    }

    /// <summary>Preset by id, or the given custom gains when the id is "custom".</summary>
    public void Set(string? presetId, ReadOnlySpan<double> customGains)
    {
        var preset = EqPresets.FromId(presetId);
        if (preset == EqPreset.Custom) SetGains(customGains);
        else SetPreset(preset);
    }

    private void Apply(ReadOnlySpan<double> gainsDb)
    {
        Span<double> want = stackalloc double[BandCount];
        for (int i = 0; i < BandCount; i++)
            want[i] = i < gainsDb.Length && double.IsFinite(gainsDb[i]) ? Math.Clamp(gainsDb[i], -18, 18) : 0;
        want.CopyTo(_gains);

        // Linear first guess, then a few Newton-like corrections with the fixed interaction matrix: the dB
        // response is only approximately linear in the gain, so this lands within a few hundredths of a dB.
        Span<double> g = stackalloc double[BandCount];
        Span<double> err = stackalloc double[BandCount];
        Span<double> step = stackalloc double[BandCount];
        MultiplyInverse(want, g);
        for (int iter = 0; iter < 6; iter++)
        {
            for (int i = 0; i < BandCount; i++)
            {
                double r = 0;
                for (int j = 0; j < BandCount; j++) r += BandResponseDb(j, g[j], Bands[i].Control);
                err[i] = want[i] - r;
            }
            MultiplyInverse(err, step);
            for (int j = 0; j < BandCount; j++) g[j] = Math.Clamp(g[j] + step[j], -MaxInternalDb, MaxInternalDb);
        }
        for (int j = 0; j < BandCount; j++)
        {
            if (Math.Abs(g[j]) < 1e-6) g[j] = 0;
            Volatile.Write(ref _target[j], g[j]);
        }
    }

    private void MultiplyInverse(ReadOnlySpan<double> v, Span<double> result)
    {
        for (int i = 0; i < BandCount; i++)
        {
            double s = 0;
            for (int j = 0; j < BandCount; j++) s += _inverse[i, j] * v[j];
            result[i] = s;
        }
    }

    /// <summary>Magnitude response in dB at <paramref name="frequency"/> for the current settings (not the glide).</summary>
    public double MagnitudeDb(double frequency)
    {
        double sum = 0;
        for (int j = 0; j < BandCount; j++) sum += BandResponseDb(j, Volatile.Read(ref _target[j]), frequency);
        return sum;
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

    /// <summary>Clears the filter state (on the audio thread, at the next Process).</summary>
    public void Reset() => _resetRequested = true;

    public void Process(Span<float> interleaved, int frames)
    {
        if (_resetRequested)
        {
            _resetRequested = false;
            Array.Clear(_zl1); Array.Clear(_zl2); Array.Clear(_zr1); Array.Clear(_zr2);
        }
        if (!_started)
        {
            // The first block starts at the configured curve instead of gliding from flat.
            _started = true;
            for (int i = 0; i < BandCount; i++)
            {
                _current[i] = Volatile.Read(ref _target[i]);
                Coefficients(i, _current[i], out _b0[i], out _b1[i], out _b2[i], out _a1[i], out _a2[i]);
            }
        }

        for (int pos = 0; pos < frames; pos += Block)
        {
            int n = Math.Min(Block, frames - pos);
            bool identity = Glide();
            if (identity)
            {
                // Flat: let the filter memory ring out, then bypass (bit-exact) with cleared state.
                if (_identityFrames > 4096)
                {
                    CommitRamps();
                    continue;
                }
                _identityFrames += n;
                if (_identityFrames > 4096)
                {
                    Array.Clear(_zl1); Array.Clear(_zl2); Array.Clear(_zr1); Array.Clear(_zr2);
                    CommitRamps();
                    continue;
                }
            }
            else
            {
                _identityFrames = 0;
            }
            Filter(interleaved.Slice(pos * 2, n * 2), n);
            CommitRamps();
        }
    }

    /// <summary>
    /// Move the gains one block towards their targets. A band that moves gets block-end coefficients in
    /// _n*, and Filter ramps to them sample by sample (no zipper at the block rate). True when every band is flat.
    /// </summary>
    private bool Glide()
    {
        bool identity = true;
        for (int i = 0; i < BandCount; i++)
        {
            double target = Volatile.Read(ref _target[i]);
            double cur = _current[i];
            _ramping[i] = cur != target;
            if (_ramping[i])
            {
                cur += (target - cur) * _smooth;
                if (Math.Abs(target - cur) < 0.002) cur = target;
                _current[i] = cur;
                Coefficients(i, cur, out _nb0[i], out _nb1[i], out _nb2[i], out _na1[i], out _na2[i]);
            }
            if (cur != 0) identity = false;
        }
        return identity;
    }

    /// <summary>Make the block-end coefficients current (also when the block was bypassed).</summary>
    private void CommitRamps()
    {
        for (int i = 0; i < BandCount; i++)
        {
            if (!_ramping[i]) continue;
            _ramping[i] = false;
            (_b0[i], _b1[i], _b2[i], _a1[i], _a2[i]) = (_nb0[i], _nb1[i], _nb2[i], _na1[i], _na2[i]);
        }
    }

    private void Filter(Span<float> x, int frames)
    {
        for (int band = 0; band < BandCount; band++)
        {
            bool ramp = _ramping[band];
            if (!ramp && _current[band] == 0 && _zl1[band] == 0 && _zl2[band] == 0 && _zr1[band] == 0 && _zr2[band] == 0) continue;
            double b0 = _b0[band], b1 = _b1[band], b2 = _b2[band], a1 = _a1[band], a2 = _a2[band];
            double db0 = 0, db1 = 0, db2 = 0, da1 = 0, da2 = 0;
            if (ramp)
            {
                double k = 1.0 / frames;
                db0 = (_nb0[band] - b0) * k; db1 = (_nb1[band] - b1) * k; db2 = (_nb2[band] - b2) * k;
                da1 = (_na1[band] - a1) * k; da2 = (_na2[band] - a2) * k;
            }
            double l1 = _zl1[band], l2 = _zl2[band], r1 = _zr1[band], r2 = _zr2[band];
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
            // Flush tiny state so silence never decays into (slow) subnormal arithmetic.
            _zl1[band] = Math.Abs(l1) < 1e-25 ? 0 : l1;
            _zl2[band] = Math.Abs(l2) < 1e-25 ? 0 : l2;
            _zr1[band] = Math.Abs(r1) < 1e-25 ? 0 : r1;
            _zr2[band] = Math.Abs(r2) < 1e-25 ? 0 : r2;
        }
    }

    /// <summary>RBJ cookbook biquad for band i at the given gain, normalized so a0 = 1.</summary>
    private void Coefficients(int i, double gainDb, out double b0, out double b1, out double b2, out double a1, out double a2)
    {
        double cos = _cos[i], sin = _sin[i];
        double alpha = sin / (2 * Bands[i].Q);
        double a = Math.Pow(10, gainDb / 40);
        double a0;
        switch (Bands[i].Kind)
        {
            case Kind.Peak:
                b0 = 1 + alpha * a;
                b1 = -2 * cos;
                b2 = 1 - alpha * a;
                a0 = 1 + alpha / a;
                a1 = -2 * cos;
                a2 = 1 - alpha / a;
                break;
            case Kind.LowShelf:
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
            default:
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
        }
        b0 /= a0; b1 /= a0; b2 /= a0; a1 /= a0; a2 /= a0;
    }

    private double BandResponseDb(int band, double gainDb, double frequency)
    {
        if (gainDb == 0) return 0;
        Coefficients(band, gainDb, out var b0, out var b1, out var b2, out var a1, out var a2);
        double w = 2 * Math.PI * frequency / _rate;
        double c1 = Math.Cos(w), s1 = Math.Sin(w), c2 = Math.Cos(2 * w), s2 = Math.Sin(2 * w);
        double nr = b0 + b1 * c1 + b2 * c2, ni = -(b1 * s1 + b2 * s2);
        double dr = 1 + a1 * c1 + a2 * c2, di = -(a1 * s1 + a2 * s2);
        return 10 * Math.Log10((nr * nr + ni * ni) / (dr * dr + di * di));
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
