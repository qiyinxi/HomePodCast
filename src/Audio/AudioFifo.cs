using System.Diagnostics;

namespace HomePodCast.Audio;

/// <summary>
/// Stereo float FIFO between capture (producer) and the RTP sender (consumer), at the stream rate.
/// Keeps latency bounded: overflow trims back to the target; underflow pads silence and re-primes.
/// </summary>
/// <remarks>
/// An empty FIFO is only a dropout if the producer comes back quickly: WASAPI loopback stops delivering
/// altogether while nothing plays, so "the music was paused" also drains it. The FIFO therefore decides when
/// data resumes: within <see cref="DropoutWindowMs"/> it was a dropout (counted in <see cref="Underruns"/>, and
/// with an adaptive target the target grows), later it was the source going quiet (<see cref="IdleGaps"/>).
/// </remarks>
public sealed class AudioFifo
{
    /// <summary>Data that resumes within this time after the FIFO ran dry means an audible dropout.</summary>
    public const int DropoutWindowMs = 200;

    private readonly float[] _buf;
    private readonly int _capacity; // frames
    private readonly int _msFrames;
    private readonly int _capMargin;
    private readonly object _lock = new();
    private int _read;              // frame index
    private int _count;             // frames
    private bool _priming = true;
    private bool _dryPending;       // ran dry; classified at the next write
    private long _dryAt;
    private long _cleanReads;

    public int TargetFrames { get; set; }
    public int CapFrames { get; set; }

    /// <summary>The configured target; an adaptive target never shrinks below it.</summary>
    public int BaseTargetFrames { get; }

    /// <summary>
    /// Adaptive target (0 = off): each dropout raises the target by <see cref="GrowMs"/> up to this many frames;
    /// <see cref="RelaxAfterReads"/> clean reads lower it by 1 ms again, down to <see cref="BaseTargetFrames"/>.
    /// Bursty capture devices end up with just enough buffer, steady ones keep the lowest latency.
    /// </summary>
    public int MaxTargetFrames { get; set; }

    public int GrowMs { get; set; } = 4;
    public long RelaxAfterReads { get; set; } = long.MaxValue;

    /// <summary>Audible dropouts: the FIFO ran dry while the source was still playing.</summary>
    public long Underruns { get; private set; }

    /// <summary>The FIFO ran dry because the source went quiet (paused, nothing playing).</summary>
    public long IdleGaps { get; private set; }

    public long Overflows { get; private set; }
    public long DroppedFrames { get; private set; }

    /// <summary>Clock in Stopwatch ticks (tests replace it).</summary>
    internal Func<long> Clock { get; set; } = Stopwatch.GetTimestamp;

    public int Depth => Volatile.Read(ref _count);
    public double TargetMs => TargetFrames / (double)_msFrames;

    public AudioFifo(int sampleRate, int targetMs, int capMs)
    {
        _capacity = sampleRate; // 1 s
        _buf = new float[_capacity * 2];
        _msFrames = sampleRate / 1000;
        TargetFrames = BaseTargetFrames = sampleRate * targetMs / 1000;
        CapFrames = sampleRate * capMs / 1000;
        _capMargin = CapFrames - TargetFrames;
    }

    public void Write(ReadOnlySpan<float> interleaved)
    {
        int frames = interleaved.Length / 2;
        if (frames > _capacity)
        {
            interleaved = interleaved[^(_capacity * 2)..];
            frames = _capacity;
        }
        lock (_lock)
        {
            if (_dryPending) ClassifyDry();
            if (_count + frames > _capacity) Skip(_count + frames - _capacity); // ring full: oldest go first
            int write = (_read + _count) % _capacity;
            for (int i = 0; i < frames; i++)
            {
                _buf[write * 2] = interleaved[i * 2];
                _buf[write * 2 + 1] = interleaved[i * 2 + 1];
                if (++write == _capacity) write = 0;
            }
            _count += frames;

            if (_count > CapFrames)
            {
                // Too far ahead of the sender: jump back to the target so latency stays bounded.
                int drop = _count - TargetFrames;
                Skip(drop);
                Overflows++;
                DroppedFrames += drop;
            }
        }
    }

    /// <summary>Fill dest (frames*2 floats). Returns false if any silence had to be inserted.</summary>
    public bool Read(Span<float> dest)
    {
        int frames = dest.Length / 2;
        lock (_lock)
        {
            if (_priming)
            {
                if (_count < TargetFrames)
                {
                    dest.Clear();
                    return false;
                }
                _priming = false;
            }

            int n = Math.Min(frames, _count);
            for (int i = 0; i < n; i++)
            {
                dest[i * 2] = _buf[_read * 2];
                dest[i * 2 + 1] = _buf[_read * 2 + 1];
                if (++_read == _capacity) _read = 0;
            }
            _count -= n;
            if (n < frames)
            {
                dest[(n * 2)..].Clear();
                _priming = true;
                _dryPending = true;
                _dryAt = Clock();
                _cleanReads = 0;
                return false;
            }
            if (MaxTargetFrames > 0 && TargetFrames > BaseTargetFrames && ++_cleanReads >= RelaxAfterReads)
            {
                _cleanReads = 0;
                SetTarget(Math.Max(TargetFrames - _msFrames, BaseTargetFrames));
            }
            return true;
        }
    }

    /// <summary>The source is writing again after the FIFO ran dry: a dropout, or just the end of a quiet spell.</summary>
    private void ClassifyDry()
    {
        _dryPending = false;
        if (Clock() - _dryAt > DropoutWindowMs * Stopwatch.Frequency / 1000)
        {
            IdleGaps++;
            return;
        }
        Underruns++;
        if (MaxTargetFrames > TargetFrames) SetTarget(Math.Min(TargetFrames + GrowMs * _msFrames, MaxTargetFrames));
    }

    private void SetTarget(int frames)
    {
        TargetFrames = frames;
        CapFrames = frames + _capMargin;
    }

    /// <summary>After a stall, throw away anything beyond the target so we don't play stale audio.</summary>
    public void DiscardToTarget()
    {
        lock (_lock)
        {
            if (_count > TargetFrames)
            {
                DroppedFrames += _count - TargetFrames;
                Skip(_count - TargetFrames);
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _read = 0;
            _count = 0;
            _priming = true;
            _dryPending = false;
        }
    }

    private void Skip(int frames)
    {
        _read = (_read + frames) % _capacity;
        _count -= frames;
    }
}
