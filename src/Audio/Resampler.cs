namespace HomePodCast.Audio;

/// <summary>
/// Stereo windowed-sinc resampler with a continuously adjustable ratio, used both for rate conversion
/// (e.g. 48 kHz → 44.1 kHz) and for absorbing clock drift between the sound card and the network
/// timeline (a few hundred ppm) without drops or clicks.
/// </summary>
public sealed class Resampler
{
    private const int BaseHalfTaps = 32;      // 64-tap kernel
    private const int Phases = 128;           // kernel table resolution, linearly interpolated
    private const double KaiserBeta = 8.6;    // ~ -85 dB stopband

    private readonly int _halfTaps, _taps;
    private readonly float[] _table;          // (Phases + 1) rows of _taps coefficients
    private readonly double _nominalStep;     // input frames per output frame
    private float[] _hist = new float[4096 * 2];
    private int _histFrames;                  // valid frames in _hist
    private double _pos;                      // position of the next output frame within _hist

    /// <summary>Relative speed correction, e.g. +200e-6 consumes input 200 ppm faster.</summary>
    public double Adjust { get; set; }

    public int InputRate { get; }
    public int OutputRate { get; }

    /// <summary>
    /// Delay the kernel adds, in input frames (half its length): the first output frame sits at input frame
    /// DelayFrames - 1, and DelayFrames input frames of look-ahead are held back.
    /// </summary>
    public int DelayFrames => _halfTaps;

    public Resampler(int inputRate, int outputRate)
    {
        InputRate = inputRate;
        OutputRate = outputRate;
        _nominalStep = (double)inputRate / outputRate;

        // The window's transition band is a fixed share of the input rate, so with 64 taps a 96 or 192 kHz input
        // rolled off from 16 or 12 kHz (2026-10-27 review). Decimating by 1.5 or more, the kernel grows with the
        // ratio and keeps the transition about 3.8 kHz wide at the output rate; 44.1/48 kHz (the realtime path,
        // the drift resamplers) keep the 64-tap kernel and its latency.
        double ratio = (double)inputRate / outputRate;
        _halfTaps = ratio < 1.5 ? BaseHalfTaps : (int)Math.Ceiling(BaseHalfTaps * ratio);
        _taps = _halfTaps * 2;

        // Cutoff relative to the input rate, a bit below the lower Nyquist to leave a transition band.
        double cutoff = 0.5 * Math.Min(1.0, (double)outputRate / inputRate) * 0.92;
        _table = new float[(Phases + 1) * _taps];
        for (int p = 0; p <= Phases; p++)
        {
            double frac = (double)p / Phases;
            double sum = 0;
            for (int t = 0; t < _taps; t++)
            {
                double x = t - (_halfTaps - 1) - frac; // distance from the output point, in input frames
                double h = 2 * cutoff * Sinc(2 * cutoff * x) * Kaiser(x / _halfTaps);
                _table[p * _taps + t] = (float)h;
                sum += h;
            }
            for (int t = 0; t < _taps; t++) _table[p * _taps + t] = (float)(_table[p * _taps + t] / sum);
        }
        _pos = _halfTaps - 1;
        _histFrames = 0;
    }

    private static double Sinc(double x) => Math.Abs(x) < 1e-9 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);

    private static double Kaiser(double x)
    {
        if (Math.Abs(x) >= 1) return 0;
        return BesselI0(KaiserBeta * Math.Sqrt(1 - x * x)) / BesselI0(KaiserBeta);
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, half = x / 2;
        for (int k = 1; k < 50; k++)
        {
            term *= half / k * (half / k);
            sum += term;
            if (term < 1e-12 * sum) break;
        }
        return sum;
    }

    /// <summary>Push interleaved stereo input; appends produced interleaved stereo frames to output.</summary>
    public void Process(ReadOnlySpan<float> input, List<float> output)
    {
        int inFrames = input.Length / 2;
        EnsureCapacity(_histFrames + inFrames);
        input.CopyTo(_hist.AsSpan(_histFrames * 2));
        _histFrames += inFrames;

        double step = _nominalStep * (1.0 + Adjust);
        var table = _table;
        var hist = _hist;
        int halfTaps = _halfTaps, taps = _taps;
        while (true)
        {
            int ip = (int)_pos;
            if (ip + halfTaps >= _histFrames) break; // need halfTaps frames of look-ahead
            double frac = _pos - ip;
            double phase = frac * Phases;
            int p0 = (int)phase;
            float w1 = (float)(phase - p0), w0 = 1f - w1;
            int row0 = p0 * taps, row1 = row0 + taps;
            int start = ip - (halfTaps - 1);

            float l = 0, r = 0;
            for (int t = 0; t < taps; t++)
            {
                float c = table[row0 + t] * w0 + table[row1 + t] * w1;
                int idx = (start + t) * 2;
                l += hist[idx] * c;
                r += hist[idx + 1] * c;
            }
            output.Add(l);
            output.Add(r);
            _pos += step;
        }

        // Drop consumed history, keeping halfTaps frames behind the read point.
        int keepFrom = Math.Max(0, (int)_pos - (halfTaps - 1));
        if (keepFrom > 0)
        {
            int remain = _histFrames - keepFrom;
            if (remain > 0) Array.Copy(_hist, keepFrom * 2, _hist, 0, remain * 2);
            _histFrames = Math.Max(0, remain);
            _pos -= keepFrom;
        }
    }

    public void Reset()
    {
        _histFrames = 0;
        _pos = _halfTaps - 1;
    }

    private void EnsureCapacity(int frames)
    {
        if (frames * 2 <= _hist.Length) return;
        Array.Resize(ref _hist, Math.Max(frames * 2, _hist.Length * 2));
    }
}
