namespace HomePodCast.Audio;

/// <summary>
/// Stereo float FIFO between capture (producer) and the RTP sender (consumer), at the stream rate.
/// Keeps latency bounded: overflow trims back to the target; underflow pads silence and re-primes.
/// </summary>
public sealed class AudioFifo
{
    private readonly float[] _buf;
    private readonly int _capacity; // frames
    private readonly object _lock = new();
    private int _read;              // frame index
    private int _count;             // frames
    private bool _priming = true;

    public int TargetFrames { get; set; }
    public int CapFrames { get; set; }

    public long Underruns { get; private set; }
    public long Overflows { get; private set; }
    public long DroppedFrames { get; private set; }

    public int Depth => Volatile.Read(ref _count);

    public AudioFifo(int sampleRate, int targetMs, int capMs)
    {
        _capacity = sampleRate; // 1 s
        _buf = new float[_capacity * 2];
        TargetFrames = sampleRate * targetMs / 1000;
        CapFrames = sampleRate * capMs / 1000;
    }

    public void Write(ReadOnlySpan<float> interleaved)
    {
        int frames = interleaved.Length / 2;
        lock (_lock)
        {
            if (_count + frames > CapFrames)
            {
                int drop = Math.Min(_count, _count + frames - TargetFrames);
                Skip(drop);
                Overflows++;
                DroppedFrames += drop;
            }
            int write = (_read + _count) % _capacity;
            for (int i = 0; i < frames; i++)
            {
                _buf[write * 2] = interleaved[i * 2];
                _buf[write * 2 + 1] = interleaved[i * 2 + 1];
                if (++write == _capacity) write = 0;
            }
            _count = Math.Min(_count + frames, _capacity);
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
                Underruns++;
                _priming = true;
                return false;
            }
            return true;
        }
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
        }
    }

    private void Skip(int frames)
    {
        _read = (_read + frames) % _capacity;
        _count -= frames;
    }
}
