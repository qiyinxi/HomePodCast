namespace HomePodCast.Audio;

/// <summary>
/// Stereo windowed-sinc resampler with a continuously adjustable ratio, used both for rate conversion
/// (e.g. 48 kHz → 44.1 kHz) and for absorbing clock drift between the sound card and the network
/// timeline (a few hundred ppm) without drops or clicks.
/// </summary>
public sealed class Resampler
{
    private const int HalfTaps = 32;          // 64-tap kernel
    private const int Taps = HalfTaps * 2;
    private const int Phases = 128;           // kernel table resolution, linearly interpolated
    private const double KaiserBeta = 8.6;    // ~ -85 dB stopband

    private readonly float[] _table;          // (Phases + 1) rows of Taps coefficients
    private readonly double _nominalStep;     // input frames per output frame
    private float[] _hist = new float[4096 * 2];
    private int _histFrames;                  // valid frames in _hist
    private double _pos;                      // position of the next output frame within _hist

    /// <summary>Relative speed correction, e.g. +200e-6 consumes input 200 ppm faster.</summary>
    public double Adjust { get; set; }

    public int InputRate { get; }
    public int OutputRate { get; }

    public Resampler(int inputRate, int outputRate)
    {
        InputRate = inputRate;
        OutputRate = outputRate;
        _nominalStep = (double)inputRate / outputRate;

        // Cutoff relative to the input rate, a bit below the lower Nyquist to leave a transition band.
        double cutoff = 0.5 * Math.Min(1.0, (double)outputRate / inputRate) * 0.92;
        _table = new float[(Phases + 1) * Taps];
        for (int p = 0; p <= Phases; p++)
        {
            double frac = (double)p / Phases;
            double sum = 0;
            for (int t = 0; t < Taps; t++)
            {
                double x = t - (HalfTaps - 1) - frac; // distance from the output point, in input frames
                double h = 2 * cutoff * Sinc(2 * cutoff * x) * Kaiser(x / HalfTaps);
                _table[p * Taps + t] = (float)h;
                sum += h;
            }
            for (int t = 0; t < Taps; t++) _table[p * Taps + t] = (float)(_table[p * Taps + t] / sum);
        }
        _pos = HalfTaps - 1;
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
        while (true)
        {
            int ip = (int)_pos;
            if (ip + HalfTaps >= _histFrames) break; // need HalfTaps frames of look-ahead
            double frac = _pos - ip;
            double phase = frac * Phases;
            int p0 = (int)phase;
            float w1 = (float)(phase - p0), w0 = 1f - w1;
            int row0 = p0 * Taps, row1 = row0 + Taps;
            int start = ip - (HalfTaps - 1);

            float l = 0, r = 0;
            for (int t = 0; t < Taps; t++)
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

        // Drop consumed history, keeping HalfTaps frames behind the read point.
        int keepFrom = Math.Max(0, (int)_pos - (HalfTaps - 1));
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
        _pos = HalfTaps - 1;
    }

    private void EnsureCapacity(int frames)
    {
        if (frames * 2 <= _hist.Length) return;
        Array.Resize(ref _hist, Math.Max(frames * 2, _hist.Length * 2));
    }
}
