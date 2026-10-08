using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HomePodCast.Audio;

/// <summary>A live stereo source that consumers subscribe to with their own <see cref="AudioTap"/>.</summary>
public interface ITapSource
{
    /// <summary>Rate of the interleaved stereo audio pushed into attached taps.</summary>
    int Rate { get; }

    /// <summary>Average time from the sound reaching the input to it being pushed into the taps (ms).</summary>
    double LatencyMs { get; }

    /// <summary>Typical interval between pushes (ms): a consumer's FIFO must cover it.</summary>
    double ChunkMs { get; }

    void Attach(AudioTap tap);
    void Detach(AudioTap tap);
}

/// <summary>
/// Stereo float FIFO between a live source (producer thread) and one consumer (the HomePod mixer, the
/// local monitor), in the style of <see cref="AudioFifo"/>. The producer side runs a resampler that
/// converts to the consumer's rate and is steered by the FIFO depth (PI control, ±1000 ppm), so two
/// unsynchronized clocks never drift apart and the depth stays near the target without drops.
/// </summary>
public sealed class AudioTap
{
    private const double Kp = 0.1, Ki = 0.002, MaxAdjust = 1000e-6;

    private readonly float[] _buf;
    private readonly int _capacity;             // frames
    private readonly object _lock = new();
    private readonly Resampler _resampler;
    private readonly List<float> _resampled;
    private int _read, _count;
    private bool _priming = true;
    private volatile bool _resetResampler;
    private double _avgDepth, _integral;
    private long _lastSteer, _lastWrite, _lastRead;

    /// <summary>Time source (QPC ticks); tests replace it to simulate hours of drift in milliseconds.</summary>
    internal Func<long> Clock = Stopwatch.GetTimestamp;

    public int InputRate { get; }
    public int OutputRate { get; }

    /// <summary>
    /// Depth to fill before releasing audio, and the average depth (seen before each read) the drift
    /// control holds, in frames. Must exceed one read plus half a write plus jitter, or reads underrun.
    /// </summary>
    public int TargetFrames { get; set; }

    /// <summary>Above this depth the oldest audio is dropped back to the target (frames).</summary>
    public int CapFrames { get; set; }

    public long Underruns { get; private set; }
    public long Overflows { get; private set; }
    public long DroppedFrames { get; private set; }
    public double DriftPpm { get; private set; }
    public int Depth => Volatile.Read(ref _count);

    /// <summary>Smoothed depth seen by the consumer just before its reads (frames).</summary>
    public double AverageDepth => _avgDepth;

    /// <summary>Delay added by the resampler (≈ half its kernel), in ms.</summary>
    public double ResamplerDelayMs => 32 * 1000.0 / InputRate;

    /// <summary>True when the consumer read in the last second (e.g. the HomePod is actually streaming).</summary>
    public bool IsBeingRead => Clock() - Volatile.Read(ref _lastRead) < Stopwatch.Frequency;

    /// <summary>True when the producer wrote in the last 200 ms.</summary>
    public bool IsBeingWritten => Clock() - Volatile.Read(ref _lastWrite) < Stopwatch.Frequency / 5;

    public AudioTap(int inputRate, int outputRate, int targetMs, int capMs)
    {
        InputRate = inputRate;
        OutputRate = outputRate;
        _capacity = outputRate; // 1 s
        _buf = new float[_capacity * 2];
        TargetFrames = outputRate * targetMs / 1000;
        CapFrames = outputRate * capMs / 1000;
        _avgDepth = TargetFrames;
        _resampler = new Resampler(inputRate, outputRate);
        _resampled = new List<float>(outputRate / 5 * 2);
    }

    /// <summary>Producer: push interleaved stereo at <see cref="InputRate"/>. Allocation-free for chunks under 100 ms.</summary>
    public void Write(ReadOnlySpan<float> interleaved)
    {
        if (_resetResampler)
        {
            _resetResampler = false;
            _resampler.Reset();
            _resampler.Adjust = 0;
            _integral = 0;
        }
        _resampled.Clear();
        _resampler.Process(interleaved, _resampled);
        var data = CollectionsMarshal.AsSpan(_resampled);
        int frames = data.Length / 2;
        if (frames > _capacity)
        {
            data = data[^(_capacity * 2)..];
            frames = _capacity;
        }
        lock (_lock)
        {
            if (_count + frames > _capacity) Skip(_count + frames - _capacity);
            int write = (_read + _count) % _capacity;
            int first = Math.Min(frames, _capacity - write);
            data[..(first * 2)].CopyTo(_buf.AsSpan(write * 2));
            data[(first * 2)..].CopyTo(_buf);
            _count += frames;
            if (_count > CapFrames)
            {
                int drop = _count - TargetFrames;
                Skip(drop);
                Overflows++;
                DroppedFrames += drop;
            }
            _lastWrite = Clock();
        }
        Steer();
    }

    /// <summary>
    /// Consumer: fill dest (interleaved stereo at <see cref="OutputRate"/>). Returns the number of frames of
    /// real audio; the rest of dest is zeroed. Returns 0 while (re)priming to the target depth.
    /// </summary>
    public int Read(Span<float> dest)
    {
        int frames = dest.Length / 2;
        lock (_lock)
        {
            _lastRead = Clock();
            if (_priming)
            {
                if (_count < TargetFrames || _count == 0)
                {
                    dest.Clear();
                    return 0;
                }
                // Start exactly at the target: a write can overshoot it by a whole packet, which the
                // ±1000 ppm drift control would need seconds to drain. Nothing has been heard yet, so
                // dropping the oldest frames here is inaudible.
                if (TargetFrames > 0 && _count > TargetFrames)
                {
                    DroppedFrames += _count - TargetFrames;
                    Skip(_count - TargetFrames);
                }
                _priming = false;
                _avgDepth = _count;
            }
            _avgDepth += 0.05 * (_count - _avgDepth);

            int n = Math.Min(frames, _count);
            int first = Math.Min(n, _capacity - _read);
            _buf.AsSpan(_read * 2, first * 2).CopyTo(dest);
            _buf.AsSpan(0, (n - first) * 2).CopyTo(dest[(first * 2)..]);
            _read = (_read + n) % _capacity;
            _count -= n;
            if (n < frames)
            {
                dest[(n * 2)..].Clear();
                Underruns++;
                _priming = true;
            }
            return n;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _read = 0;
            _count = 0;
            _priming = true;
            _avgDepth = TargetFrames;
        }
        _resetResampler = true;
    }

    /// <summary>PI control of the resampling ratio from the consumer-side depth (runs on the producer thread).</summary>
    private void Steer()
    {
        long now = Clock();
        double dt = _lastSteer == 0 ? 0 : Math.Min(0.1, (now - _lastSteer) / (double)Stopwatch.Frequency);
        _lastSteer = now;
        if (!IsBeingRead || _priming) return; // nobody consuming: nothing to track

        double error = (_avgDepth - TargetFrames) / OutputRate; // seconds; > 0 = too much buffered
        _integral = Math.Clamp(_integral + error * dt, -MaxAdjust / Ki, MaxAdjust / Ki);
        _resampler.Adjust = Math.Clamp(Kp * error + Ki * _integral, -MaxAdjust, MaxAdjust);
        DriftPpm = _resampler.Adjust * 1e6;
    }


    private void Skip(int frames)
    {
        _read = (_read + frames) % _capacity;
        _count -= frames;
    }
}
