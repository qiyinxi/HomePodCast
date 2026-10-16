using HomePodCast.Net;

namespace HomePodCast.Audio;

/// <summary>
/// Estimates a renderer's PCM consumption rate only while HTTP backpressure makes completed writes a read proxy.
/// Socket completion is not a presentation clock: initial prefill and sender-paced writes must be marked ineligible.
/// </summary>
internal sealed class DlnaReadClock
{
    public const int SampleRate = 44100;
    public const double MaximumDriftPpm = 2000;
    private const int Capacity = 256;
    private const double WindowSeconds = 60, MinimumSeconds = 45, SampleSeconds = 0.25, PauseSeconds = 2;
    private readonly long[] _ticks = new long[Capacity], _frames = new long[Capacity];
    private readonly double[] _residuals = new double[Capacity];
    private int _first, _count;
    private long _lastTicks, _lastFrames, _lastFitTicks;
    private bool _hasObservation;
    private double _driftPpm;

    public bool HasEstimate { get; private set; }
    public double DriftPpm => HasEstimate ? _driftPpm : 0;
    public double SourceFramesPerOutputFrame => 1 / (1 + DriftPpm / 1_000_000);
    /// <summary>Fit quality of the eligible window, not a claim about the receiver's unknown buffering latency.</summary>
    public double Confidence { get; private set; }

    public void Reset()
    {
        _first = _count = 0;
        _hasObservation = HasEstimate = false;
        _lastTicks = _lastFrames = _lastFitTicks = 0;
        _driftPpm = Confidence = 0;
    }

    /// <param name="completedFrames">Cumulative completed PCM frames in this HTTP connection.</param>
    /// <param name="completionTicks">Completion time on MediaClock.</param>
    /// <param name="receiverReadProxy">True only when sustained receiver backpressure, rather than sender pacing, controls writes.</param>
    public void Observe(long completedFrames, long completionTicks, bool receiverReadProxy)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(completedFrames);
        if (!receiverReadProxy)
        {
            Reset();
            return;
        }
        if (_hasObservation && (completionTicks <= _lastTicks || completedFrames < _lastFrames
            || Seconds(completionTicks - _lastTicks) > PauseSeconds)) Reset();
        _lastTicks = completionTicks;
        _lastFrames = completedFrames;
        _hasObservation = true;
        if (_count != 0 && Seconds(completionTicks - _ticks[Slot(_count - 1)]) < SampleSeconds) return;
        while (_count > 0 && Seconds(completionTicks - _ticks[_first]) > WindowSeconds)
        {
            _first = (_first + 1) % Capacity;
            _count--;
        }
        if (_count == Capacity)
        {
            _first = (_first + 1) % Capacity;
            _count--;
        }
        int slot = Slot(_count++);
        _ticks[slot] = completionTicks;
        _frames[slot] = completedFrames;
        Fit(completionTicks);
    }

    private void Fit(long now)
    {
        HasEstimate = false;
        Confidence = 0;
        if (_count < 40 || Seconds(now - _ticks[_first]) < MinimumSeconds) return;
        Regress(double.PositiveInfinity, 0, 0, out double intercept, out double slope, out _);
        for (int i = 0; i < _count; i++)
            _residuals[i] = Math.Abs(Y(i) - intercept - slope * X(i));
        _residuals.AsSpan(0, _count).Sort();
        // A delayed socket completion is not a sudden change of crystal frequency. Trim its residual, then refit.
        double threshold = Math.Max(0.015, _residuals[_count / 2] * 3);
        Regress(threshold, intercept, slope, out double cleanIntercept, out double cleanSlope, out int inliers);
        if (inliers < _count * 0.8) return;
        double squareError = 0;
        for (int i = 0; i < _count; i++)
        {
            if (Math.Abs(Y(i) - intercept - slope * X(i)) > threshold) continue;
            double error = Y(i) - cleanIntercept - cleanSlope * X(i);
            squareError += error * error;
        }
        double rms = Math.Sqrt(squareError / inliers);
        double ppm = (cleanSlope - 1) * 1_000_000;
        if (!double.IsFinite(ppm) || Math.Abs(ppm) > MaximumDriftPpm || rms > 0.08) return;
        Confidence = inliers / (double)_count * Math.Max(0, 1 - rms / 0.16);
        if (Confidence < 0.5) return;
        // The regression window removes packet jitter; this slower average keeps moving burst boundaries from
        // modulating the source position. The first valid window can be used directly after its 45-second warm-up.
        double weight = _lastFitTicks == 0 ? 1 : 1 - Math.Exp(-Seconds(now - _lastFitTicks) / 60);
        _driftPpm += weight * (ppm - _driftPpm);
        _lastFitTicks = now;
        HasEstimate = true;
    }

    private void Regress(double threshold, double previousIntercept, double previousSlope,
        out double intercept, out double slope, out int count)
    {
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        count = 0;
        for (int i = 0; i < _count; i++)
        {
            double x = X(i), y = Y(i);
            if (Math.Abs(y - previousIntercept - previousSlope * x) > threshold) continue;
            sx += x;
            sy += y;
            sxx += x * x;
            sxy += x * y;
            count++;
        }
        double denominator = count * sxx - sx * sx;
        slope = denominator > 0 ? (count * sxy - sx * sy) / denominator : double.NaN;
        intercept = (sy - slope * sx) / count;
    }

    private int Slot(int index) => (_first + index) % Capacity;
    private double X(int index) => Seconds(_ticks[Slot(index)] - _ticks[_first]);
    private double Y(int index) => (_frames[Slot(index)] - _frames[_first]) / (double)SampleRate;
    private static double Seconds(long ticks) => ticks / (double)MediaClock.Frequency;
}
