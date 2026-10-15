using HomePodCast.Net;

namespace HomePodCast.Audio;

/// <summary>
/// Resamples the shared history at an endpoint's actual presentation clock. The centered sinc has no added phase
/// delay; the group must leave enough headroom for its 32-frame look-ahead. All storage is allocated on connection.
/// </summary>
internal sealed class ClockedTimelineReader
{
    private const int HalfTaps = 32, Taps = 64, Phases = 128;
    private readonly TimelineBuffer _timeline;
    private readonly int _outputRate, _maxFrames;
    private readonly float[] _scratch, _table = new float[(Phases + 1) * Taps];

    public ClockedTimelineReader(TimelineBuffer timeline, int outputRate, int maxFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrames);
        _timeline = timeline;
        _outputRate = outputRate;
        _maxFrames = maxFrames;
        _scratch = new float[checked(((int)Math.Ceiling(maxFrames * timeline.SampleRate / (outputRate * 0.98)) + Taps + 2) * 2)];
        double cutoff = 0.5 * Math.Min(1, outputRate * 0.98 / timeline.SampleRate) * 0.92;
        for (int phase = 0; phase <= Phases; phase++)
        {
            double sum = 0;
            for (int tap = 0; tap < Taps; tap++)
            {
                double x = tap - (HalfTaps - 1) - phase / (double)Phases;
                double sinc = Math.Abs(x) < 1e-9 ? 1 : Math.Sin(2 * Math.PI * cutoff * x) / (2 * Math.PI * cutoff * x);
                double window = Math.Abs(x) >= HalfTaps ? 0 : BesselI0(8.6 * Math.Sqrt(1 - x * x / (HalfTaps * HalfTaps))) / BesselI0(8.6);
                double coefficient = 2 * cutoff * sinc * window;
                _table[phase * Taps + tap] = (float)coefficient;
                sum += coefficient;
            }
            for (int tap = 0; tap < Taps; tap++) _table[phase * Taps + tap] /= (float)sum;
        }
    }

    public bool Read(long presentationTicks, int groupDelayMs, int trimMs, double actualOutputRate, Span<float> stereo)
    {
        int frames = stereo.Length / 2;
        if (stereo.Length % 2 != 0 || frames > _maxFrames) throw new ArgumentException("Invalid output block", nameof(stereo));
        if (!double.IsFinite(actualOutputRate) || actualOutputRate < _outputRate * 0.98 || actualOutputRate > _outputRate * 1.02)
            throw new ArgumentOutOfRangeException(nameof(actualOutputRate));
        if (frames == 0) return true;
        double first = (presentationTicks - _timeline.OriginTicks) * (double)_timeline.SampleRate / MediaClock.Frequency
            - ((long)groupDelayMs + trimMs) * _timeline.SampleRate / 1000.0;
        double step = _timeline.SampleRate / actualOutputRate;
        long begin = checked((long)Math.Floor(first) - (HalfTaps - 1));
        long end = checked((long)Math.Floor(first + (frames - 1) * step) + HalfTaps);
        int count = checked((int)(end - begin + 1));
        bool complete = _timeline.ReadFrames(begin, _scratch.AsSpan(0, count * 2));
        for (int frame = 0; frame < frames; frame++)
        {
            double position = first - begin + frame * step;
            int whole = (int)position;
            double phase = (position - whole) * Phases;
            int phase0 = (int)phase;
            float w1 = (float)(phase - phase0), w0 = 1 - w1;
            int start = whole - (HalfTaps - 1);
            float left = 0, right = 0;
            for (int tap = 0; tap < Taps; tap++)
            {
                float coefficient = _table[phase0 * Taps + tap] * w0 + _table[(phase0 + 1) * Taps + tap] * w1;
                left += _scratch[(start + tap) * 2] * coefficient;
                right += _scratch[(start + tap) * 2 + 1] * coefficient;
            }
            stereo[frame * 2] = left;
            stereo[frame * 2 + 1] = right;
        }
        return complete;
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
}
