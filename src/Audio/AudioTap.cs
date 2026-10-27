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

    /// <summary>Typical interval between pushes (ms), 0 while not known yet: a consumer's FIFO must cover it.</summary>
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
    private double _writeFrames;                // average write, in output frames (0 = none yet)
    private int _configuredTarget = -1, _configuredCap; // while EnsureReadFrames has raised the target
    private int _windowNeed;                    // the largest need EnsureReadFrames saw since _windowStart
    private long _windowStart;
    private long _lastEnsure;                   // the consumer's previous EnsureReadFrames
    private double _readInterval;               // its typical interval between them, in output frames (0 = not known)

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
    public double ResamplerDelayMs => _resampler.DelayFrames * 1000.0 / InputRate;

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
            if (frames > 0) _writeFrames = _writeFrames == 0 ? frames : _writeFrames + 0.1 * (frames - _writeFrames);
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

    /// <summary>
    /// Consumer: it is about to read <paramref name="frames"/> at once. While that is more than the target covers
    /// (one read, half an average write and 2 ms of jitter), raises <see cref="TargetFrames"/> to it, and the cap to
    /// at least 40 ms above. Once a second a raised target comes down to what the last second's reads needed, at
    /// most to the configured values, which <see cref="Clear"/> restores as well; the cap comes down with it. 2026-10-27
    /// review: a buffered AirPlay 2 session reads 1024 frames per packet from the mic's 20 ms (882-frame) tap and
    /// underran on every packet (~3 ms of silence and ~3 ms of voice lost each time); a fixed larger target would add
    /// that latency to the realtime path's 352-frame reads too.
    /// <para>
    /// A read that comes more than two typical intervals after the previous one counts as one read on time: the
    /// producer filled the tap while the consumer was late, so the catch-up needs no more depth than a normal read.
    /// 2026-10-27 review: after a 70 ms stall of the mixer hub's thread, its catch-up read (3087 frames) raised the
    /// target to ~77 ms; the tap had overflowed during the stall, re-primed at that depth, and the voice stayed ~50 ms
    /// late for about a minute while the drift control drained it at 1000 ppm.
    /// </para>
    /// </summary>
    public void EnsureReadFrames(int frames)
    {
        lock (_lock)
        {
            long now = Clock();
            double write = _writeFrames > 0 ? _writeFrames : OutputRate / 100.0; // a 10 ms packet until one came
            int need = frames - LateFrames(now) + (int)Math.Ceiling(write / 2) + OutputRate / 500;
            if (now - _windowStart > Stopwatch.Frequency)
            {
                if (_configuredTarget >= 0 && _windowNeed < TargetFrames)
                {
                    // A surplus under the cap drains through the drift control (≤ 1000 ppm), without a skip. The cap
                    // comes down with the target: a larger surplus (a re-prime at a target one big read raised) is
                    // cut once by the next write instead of draining for over a minute.
                    if (_windowNeed <= _configuredTarget) RestoreTarget();
                    else (TargetFrames, CapFrames) = (_windowNeed, Math.Max(_configuredCap, _windowNeed + OutputRate / 25));
                }
                _windowStart = now;
                _windowNeed = 0;
            }
            _windowNeed = Math.Max(_windowNeed, need);
            if (need <= TargetFrames) return;
            if (_configuredTarget < 0) (_configuredTarget, _configuredCap) = (TargetFrames, CapFrames);
            TargetFrames = need;
            CapFrames = Math.Max(CapFrames, need + OutputRate / 25);
        }
    }

    /// <summary>
    /// For a read more than two typical intervals after the previous one, the frames beyond one interval, else 0.
    /// Reads up to two intervals apart (the mixer hub's two packets at once, a sender's jitter) count in full.
    /// </summary>
    private int LateFrames(long now)
    {
        double elapsed = _lastEnsure == 0 ? 0 : (now - _lastEnsure) * (double)OutputRate / Stopwatch.Frequency;
        _lastEnsure = now;
        double typical = _readInterval;
        if (elapsed > OutputRate) _readInterval = 0; // idle for a second: the next session may read at another pace
        else if (typical == 0) _readInterval = elapsed;
        // Clamped at two intervals: a stall moves the average by at most 10 %, and a consumer that now reads less
        // often is still followed within a few reads.
        else _readInterval += 0.1 * (Math.Min(elapsed, 2 * typical) - typical);
        return typical > 0 && elapsed > 2 * typical ? (int)Math.Min(elapsed - typical, OutputRate) : 0;
    }

    private void RestoreTarget()
    {
        (TargetFrames, CapFrames) = (_configuredTarget, _configuredCap);
        _configuredTarget = -1;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _read = 0;
            _count = 0;
            _priming = true;
            _writeFrames = 0;
            _windowNeed = 0;
            if (_configuredTarget >= 0) RestoreTarget();
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
        // The integral holds the clock drift. It is left alone while the output is already at its limit in the
        // error's direction. 2026-10-27 review: draining a ~47 ms surplus at 1000 ppm wound it up to its clamp, and
        // the depth then undershot the target by ~7 ms and underran.
        double integral = Math.Clamp(_integral + error * dt, -MaxAdjust / Ki, MaxAdjust / Ki);
        double adjust = Kp * error + Ki * integral;
        if (Math.Abs(adjust) < MaxAdjust || Math.Sign(adjust) != Math.Sign(error)) _integral = integral;
        _resampler.Adjust = Math.Clamp(Kp * error + Ki * _integral, -MaxAdjust, MaxAdjust);
        DriftPpm = _resampler.Adjust * 1e6;
    }


    private void Skip(int frames)
    {
        _read = (_read + frames) % _capacity;
        _count -= frames;
    }
}
