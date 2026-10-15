using HomePodCast.Net;

namespace HomePodCast.Audio;

/// <summary>
/// Bounded stereo history on MediaClock, shared by independent outputs. Reading never consumes another reader's
/// frames. Missing, overwritten and skipped frames are silence, rather than audio from a previous turn of the ring.
/// </summary>
internal sealed class TimelineBuffer
{
    private readonly float[] _samples;
    private readonly long[] _frames;
    private readonly object _lock = new();
    private long _endFrame = long.MinValue;

    public int SampleRate { get; }
    public int CapacityFrames => _frames.Length;
    public long OriginTicks { get; }

    public TimelineBuffer(int sampleRate, int capacityFrames, long originTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityFrames);
        SampleRate = sampleRate;
        OriginTicks = originTicks;
        _samples = new float[checked(capacityFrames * 2)];
        _frames = new long[capacityFrames];
        Array.Fill(_frames, long.MinValue);
    }

    /// <summary>Nearest frame on the shared clock; integer arithmetic avoids accumulating packet rounding errors.</summary>
    public long FrameAt(long ticks)
    {
        Int128 scaled = ((Int128)ticks - OriginTicks) * SampleRate;
        return checked((long)((scaled + (scaled < 0 ? -MediaClock.Frequency / 2 : MediaClock.Frequency / 2)) / MediaClock.Frequency));
    }

    public long TicksAt(long frame) => checked(OriginTicks + (long)((Int128)frame * MediaClock.Frequency / SampleRate));

    public void Write(long sourceTicks, ReadOnlySpan<float> stereo) => WriteFrames(FrameAt(sourceTicks), stereo);

    internal void WriteFrames(long firstFrame, ReadOnlySpan<float> stereo)
    {
        if (stereo.Length % 2 != 0) throw new ArgumentException("Expected complete stereo frames", nameof(stereo));
        int count = stereo.Length / 2;
        if (count == 0) return;
        long end = checked(firstFrame + count);
        lock (_lock)
        {
            // One producer owns this timeline. Accept gaps (sender stalls), but never let a late write replace newer audio.
            if (_endFrame != long.MinValue && firstFrame < _endFrame)
                throw new ArgumentOutOfRangeException(nameof(firstFrame), "Timeline writes must not overlap or go backwards");
            _endFrame = end;
            for (int i = Math.Max(0, count - CapacityFrames); i < count; i++)
            {
                long frame = firstFrame + i;
                int slot = Slot(frame);
                _samples[slot * 2] = stereo[i * 2];
                _samples[slot * 2 + 1] = stereo[i * 2 + 1];
                _frames[slot] = frame;
            }
        }
    }

    /// <summary>Read audio due at presentationTicks; a positive trim delays this output further.</summary>
    public bool ReadAt(long presentationTicks, int groupDelayMs, int trimMs, Span<float> stereo)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(groupDelayMs);
        long delayFrames = checked((long)Math.Round(((long)groupDelayMs + trimMs) * SampleRate / 1000.0));
        return ReadFrames(checked(FrameAt(presentationTicks) - delayFrames), stereo);
    }

    internal bool ReadFrames(long firstFrame, Span<float> stereo)
    {
        if (stereo.Length % 2 != 0) throw new ArgumentException("Expected complete stereo frames", nameof(stereo));
        bool complete = true;
        lock (_lock)
        {
            for (int i = 0; i < stereo.Length / 2; i++)
            {
                long frame = checked(firstFrame + i);
                int slot = Slot(frame);
                if (_endFrame != long.MinValue && frame >= _endFrame - CapacityFrames && frame < _endFrame && _frames[slot] == frame)
                {
                    stereo[i * 2] = _samples[slot * 2];
                    stereo[i * 2 + 1] = _samples[slot * 2 + 1];
                }
                else
                {
                    stereo[i * 2] = stereo[i * 2 + 1] = 0;
                    complete = false;
                }
            }
        }
        return complete;
    }

    public Reader CreateReader(long sourceTicks) => new(this, FrameAt(sourceTicks));

    private int Slot(long frame) => (int)((frame % CapacityFrames + CapacityFrames) % CapacityFrames);

    internal sealed class Reader(TimelineBuffer timeline, long firstFrame)
    {
        public long Position { get; private set; } = firstFrame;

        public bool Read(Span<float> stereo)
        {
            bool complete = timeline.ReadFrames(Position, stereo);
            Position = checked(Position + stereo.Length / 2);
            return complete;
        }

        public void Seek(long sourceTicks) => Position = timeline.FrameAt(sourceTicks);
    }
}
