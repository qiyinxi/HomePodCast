namespace HomePodCast.Audio;

/// <summary>
/// Adjustable stereo-linked feed-forward compressor for the voice chain: threshold, ratio, soft knee, attack, release and
/// make-up gain, like a sound card's or a mixer's channel compressor. The detector is the peak of a 16-frame block, the
/// gain curve runs once per block and the gain is ramped linearly across it. Parameters may be set from any thread;
/// turning it off fades the gain back to unity before bypassing. Allocation-free.
/// </summary>
public sealed class DynamicsCompressor : IAudioEffect
{
    public const int BlockFrames = 16;
    public const float FloorDb = -80f;

    private readonly int _rate;
    private volatile float _thresholdDb = -24f, _ratio = 3f, _kneeDb = 6f, _attackMs = 10f, _releaseMs = 150f, _makeupDb;
    private volatile bool _enabled;
    private bool _active;
    private float _reductionDb;     // smoothed gain reduction (≤ 0)
    private float _gain = 1f;       // linear gain at the end of the last block
    private float _meterDb;         // for the UI

    public DynamicsCompressor(int sampleRate = 44100) => _rate = sampleRate;

    public bool Enabled { get => _enabled; set => _enabled = value; }
    public float ThresholdDb { get => _thresholdDb; set => _thresholdDb = Math.Clamp(Finite(value, -24f), -60f, 0f); }
    public float Ratio { get => _ratio; set => _ratio = Math.Clamp(Finite(value, 3f), 1f, 20f); }
    public float KneeDb { get => _kneeDb; set => _kneeDb = Math.Clamp(Finite(value, 6f), 0f, 24f); }
    public float AttackMs { get => _attackMs; set => _attackMs = Math.Clamp(Finite(value, 10f), 0.1f, 200f); }
    public float ReleaseMs { get => _releaseMs; set => _releaseMs = Math.Clamp(Finite(value, 150f), 5f, 3000f); }
    public float MakeupDb { get => _makeupDb; set => _makeupDb = Math.Clamp(Finite(value, 0f), 0f, 24f); }

    /// <summary>Gain reduction now (dB, ≥ 0), decaying like a meter; 0 when off. Approximate from another thread.</summary>
    public float GainReductionDb => _meterDb;

    internal static float Finite(float value, float fallback) => float.IsFinite(value) ? value : fallback;

    /// <summary>Static curve: gain reduction (dB, ≤ 0) for an input level, with a soft knee of <paramref name="kneeDb"/>.</summary>
    public static float ReductionDb(float inputDb, float thresholdDb, float ratio, float kneeDb)
    {
        if (!(inputDb >= FloorDb)) return 0f; // also catches NaN / −∞
        float over = inputDb - thresholdDb;
        float slope = 1 / ratio - 1;
        if (kneeDb <= 0 || 2 * over >= kneeDb) return over > 0 ? slope * over : 0f;
        if (2 * over <= -kneeDb) return 0f;
        float k = over + kneeDb / 2;
        return slope * k * k / (2 * kneeDb);
    }

    public void Process(Span<float> interleaved, int frames)
    {
        bool enabled = _enabled;
        if (!enabled && !_active)
        {
            _meterDb = 0;
            return;
        }
        _active = true;
        float threshold = _thresholdDb, ratio = _ratio, knee = _kneeDb, makeup = enabled ? _makeupDb : 0f;
        float attack = Coefficient(_attackMs), release = Coefficient(_releaseMs);
        var x = interleaved[..(frames * 2)];
        for (int start = 0; start < frames; start += BlockFrames)
        {
            int n = Math.Min(BlockFrames, frames - start);
            var block = x.Slice(start * 2, n * 2);
            float peak = 0f;
            for (int i = 0; i < block.Length; i++) peak = MathF.Max(peak, MathF.Abs(block[i]));

            float target = enabled ? ReductionDb(ToDb(peak), threshold, ratio, knee) : 0f;
            float coef = target < _reductionDb ? attack : release;
            _reductionDb += (target - _reductionDb) * coef * n / BlockFrames;
            if (MathF.Abs(_reductionDb) < 1e-4f) _reductionDb = 0;

            float g0 = _gain, g1 = FromDb(_reductionDb + makeup), dg = (g1 - g0) / n;
            for (int i = 0; i < n; i++)
            {
                float g = g0 + dg * (i + 1);
                block[i * 2] *= g;
                block[i * 2 + 1] *= g;
            }
            _gain = g1;
        }
        _meterDb = MathF.Max(-_reductionDb, _meterDb * 0.9f);
        if (!enabled && MathF.Abs(_gain - 1) < 1e-4f && _reductionDb == 0)
        {
            _gain = 1f;
            _active = false;
        }
    }

    private float Coefficient(float ms) => 1f - MathF.Exp(-BlockFrames / (ms / 1000f * _rate));

    internal static float ToDb(float amplitude) => 20f * MathF.Log10(MathF.Max(amplitude, 1e-7f));

    internal static float FromDb(float db) => MathF.Exp(db * (MathF.Log(10f) / 20f));
}

/// <summary>
/// De-esser as a dynamic high shelf: a high-pass side chain at <see cref="FrequencyHz"/> measures the sibilance band; when
/// its peak passes <see cref="ThresholdDb"/>, a high shelf at that frequency turns the band down (4:1, at most
/// <see cref="RangeDb"/>), leaving the voice below it alone. Fast attack (1 ms), 60 ms release. While nothing is reduced
/// the shelf is flat (the input passes unchanged). Allocation-free.
/// <para>
/// 2026-10-27: subtracting a turned-down high-pass band from the input reduced an 8 kHz tone by 2 dB instead of 10 (the
/// high-pass shifts the phase near its corner, so the subtraction doesn't cancel); the shelf's magnitude is what it says.
/// </para>
/// </summary>
public sealed class DeEsser : IAudioEffect
{
    private const int Block = 16;
    private const float Ratio = 4f;
    private const double ShelfQ = 0.707;

    private readonly int _rate;
    private readonly float _attack, _release;
    private volatile float _frequencyHz = 6000f, _thresholdDb = -30f, _rangeDb = 8f;
    private volatile bool _enabled;
    private float _designedHz;
    private double _cos, _sin;
    private double _b0, _b1, _b2, _a1, _a2;     // side-chain high-pass
    private double _s1, _s2;                    // its state (mid channel)
    private double _sb0 = 1, _sb1, _sb2, _sa1, _sa2; // the shelf, current
    private double _l1, _l2, _r1, _r2;          // its state
    private float _reductionDb, _shelfDb, _meterDb;
    private bool _active;

    public DeEsser(int sampleRate = 44100)
    {
        _rate = sampleRate;
        _attack = 1f - MathF.Exp(-Block / (0.001f * sampleRate));
        _release = 1f - MathF.Exp(-Block / (0.06f * sampleRate));
        Design(_frequencyHz);
    }

    public bool Enabled { get => _enabled; set => _enabled = value; }
    public float FrequencyHz { get => _frequencyHz; set => _frequencyHz = Math.Clamp(DynamicsCompressor.Finite(value, 6000f), 2000f, 12000f); }
    public float ThresholdDb { get => _thresholdDb; set => _thresholdDb = Math.Clamp(DynamicsCompressor.Finite(value, -30f), -60f, 0f); }
    public float RangeDb { get => _rangeDb; set => _rangeDb = Math.Clamp(DynamicsCompressor.Finite(value, 8f), 0f, 24f); }

    /// <summary>Reduction of the sibilance band now (dB, ≥ 0), decaying like a meter.</summary>
    public float GainReductionDb => _meterDb;

    private void Design(float hz)
    {
        _designedHz = hz;
        double w0 = 2 * Math.PI * Math.Min(hz, _rate * 0.45) / _rate;
        _cos = Math.Cos(w0);
        _sin = Math.Sin(w0);
        double alpha = _sin / (2 * 0.7071067811865476), a0 = 1 + alpha;
        _b0 = (1 + _cos) / 2 / a0; _b1 = -(1 + _cos) / a0; _b2 = (1 + _cos) / 2 / a0;
        _a1 = -2 * _cos / a0; _a2 = (1 - alpha) / a0;
    }

    /// <summary>RBJ high shelf at the de-esser frequency with <paramref name="gainDb"/> (flat at 0 dB).</summary>
    private void Shelf(double gainDb, out double b0, out double b1, out double b2, out double a1, out double a2)
    {
        if (gainDb == 0)
        {
            (b0, b1, b2, a1, a2) = (1, 0, 0, 0, 0);
            return;
        }
        double a = Math.Pow(10, gainDb / 40), s = 2 * Math.Sqrt(a) * (_sin / (2 * ShelfQ)), cos = _cos;
        double a0 = (a + 1) - (a - 1) * cos + s;
        b0 = a * ((a + 1) + (a - 1) * cos + s) / a0;
        b1 = -2 * a * ((a - 1) + (a + 1) * cos) / a0;
        b2 = a * ((a + 1) + (a - 1) * cos - s) / a0;
        a1 = 2 * ((a - 1) - (a + 1) * cos) / a0;
        a2 = ((a + 1) - (a - 1) * cos - s) / a0;
    }

    public void Process(Span<float> interleaved, int frames)
    {
        bool enabled = _enabled;
        if (!enabled && !_active)
        {
            _meterDb = 0;
            _s1 = _s2 = _l1 = _l2 = _r1 = _r2 = 0;
            return;
        }
        _active = true;
        if (_frequencyHz != _designedHz) Design(_frequencyHz); // the band moves; the state carries over (no click at these rates)
        float threshold = _thresholdDb, range = _rangeDb;
        var x = interleaved[..(frames * 2)];
        for (int start = 0; start < frames; start += Block)
        {
            int n = Math.Min(Block, frames - start);
            var block = x.Slice(start * 2, n * 2);

            // Side chain: the band's peak, on the mid channel.
            float peak = 0f;
            double s1 = _s1, s2 = _s2;
            for (int i = 0; i < n; i++)
            {
                double mid = (block[i * 2] + block[i * 2 + 1]) * 0.5;
                double h = _b0 * mid + s1;
                s1 = _b1 * mid - _a1 * h + s2;
                s2 = _b2 * mid - _a2 * h;
                peak = MathF.Max(peak, MathF.Abs((float)h));
            }
            _s1 = Flush(s1); _s2 = Flush(s2);

            float over = DynamicsCompressor.ToDb(peak) - threshold;
            float target = enabled && over > 0 ? -MathF.Min(range, over * (1 - 1 / Ratio)) : 0f;
            _reductionDb += (target - _reductionDb) * (target < _reductionDb ? _attack : _release) * n / Block;
            if (MathF.Abs(_reductionDb) < 1e-3f) _reductionDb = 0;

            // The shelf follows the reduction, its coefficients ramped across the block.
            bool moving = _reductionDb != _shelfDb;
            if (!moving && _shelfDb == 0 && _l1 == 0 && _l2 == 0 && _r1 == 0 && _r2 == 0) continue; // flat: unchanged
            double b0 = _sb0, b1 = _sb1, b2 = _sb2, a1 = _sa1, a2 = _sa2;
            double db0 = 0, db1 = 0, db2 = 0, da1 = 0, da2 = 0;
            if (moving)
            {
                Shelf(_reductionDb, out var nb0, out var nb1, out var nb2, out var na1, out var na2);
                double k = 1.0 / n;
                db0 = (nb0 - b0) * k; db1 = (nb1 - b1) * k; db2 = (nb2 - b2) * k; da1 = (na1 - a1) * k; da2 = (na2 - a2) * k;
                (_sb0, _sb1, _sb2, _sa1, _sa2) = (nb0, nb1, nb2, na1, na2);
                _shelfDb = _reductionDb;
            }
            double l1 = _l1, l2 = _l2, r1 = _r1, r2 = _r2;
            for (int i = 0; i < n; i++)
            {
                b0 += db0; b1 += db1; b2 += db2; a1 += da1; a2 += da2;
                double inL = block[i * 2], inR = block[i * 2 + 1];
                double outL = b0 * inL + l1;
                l1 = b1 * inL - a1 * outL + l2;
                l2 = b2 * inL - a2 * outL;
                double outR = b0 * inR + r1;
                r1 = b1 * inR - a1 * outR + r2;
                r2 = b2 * inR - a2 * outR;
                block[i * 2] = (float)outL;
                block[i * 2 + 1] = (float)outR;
            }
            _l1 = Flush(l1); _l2 = Flush(l2); _r1 = Flush(r1); _r2 = Flush(r2);
        }
        _meterDb = MathF.Max(-_reductionDb, _meterDb * 0.9f);
        if (!enabled && _reductionDb == 0 && _shelfDb == 0)
        {
            _active = false;
            _s1 = _s2 = _l1 = _l2 = _r1 = _r2 = 0;
        }
    }

    private static double Flush(double v) => Math.Abs(v) < 1e-25 ? 0 : v;
}

/// <summary>
/// Peak limiter for the end of the voice chain: never lets a sample past <see cref="CeilingDb"/>. No look-ahead (it would
/// add latency to the voice): the gain drops at once to what the loudest sample needs and recovers over
/// <see cref="ReleaseMs"/>. Allocation-free; off bypasses (after the gain has recovered).
/// </summary>
public sealed class PeakLimiter : IAudioEffect
{
    private readonly int _rate;
    private volatile float _ceilingDb = -1f, _releaseMs = 80f;
    private volatile bool _enabled;
    private float _gain = 1f, _meterDb;
    private bool _active;

    public PeakLimiter(int sampleRate = 44100) => _rate = sampleRate;

    public bool Enabled { get => _enabled; set => _enabled = value; }
    public float CeilingDb { get => _ceilingDb; set => _ceilingDb = Math.Clamp(DynamicsCompressor.Finite(value, -1f), -24f, 0f); }
    public float ReleaseMs { get => _releaseMs; set => _releaseMs = Math.Clamp(DynamicsCompressor.Finite(value, 80f), 5f, 2000f); }

    /// <summary>Gain reduction now (dB, ≥ 0), decaying like a meter.</summary>
    public float GainReductionDb => _meterDb;

    public void Process(Span<float> interleaved, int frames)
    {
        bool enabled = _enabled;
        if (!enabled && !_active)
        {
            _meterDb = 0;
            return;
        }
        _active = true;
        float ceiling = enabled ? DynamicsCompressor.FromDb(_ceilingDb) : float.MaxValue;
        float release = 1f - MathF.Exp(-1f / (_releaseMs / 1000f * _rate));
        float g = _gain, lowest = 1f;
        var x = interleaved[..(frames * 2)];
        for (int i = 0; i < frames; i++)
        {
            float l = x[i * 2], r = x[i * 2 + 1];
            float peak = MathF.Max(MathF.Abs(l), MathF.Abs(r));
            g += (1f - g) * release;
            if (peak * g > ceiling) g = ceiling / peak;
            x[i * 2] = Math.Clamp(l * g, -ceiling, ceiling);
            x[i * 2 + 1] = Math.Clamp(r * g, -ceiling, ceiling);
            lowest = MathF.Min(lowest, g);
        }
        _gain = g > 1 - 1e-6f ? 1f : g;
        _meterDb = MathF.Max(-DynamicsCompressor.ToDb(lowest), _meterDb * 0.9f);
        if (!enabled && _gain == 1f) _active = false;
    }
}
