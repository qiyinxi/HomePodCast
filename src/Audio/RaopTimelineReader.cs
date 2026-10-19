using HomePodCast.Net;
using HomePodCast.Protocol;

namespace HomePodCast.Audio;

/// <summary>Reads shared source history at RAOP's presentation time without consuming or processing it again.</summary>
internal sealed class RaopTimelineReader
{
    private readonly TimelineBuffer _timeline;
    private readonly long _transportLatencyTicks, _delayFrames;
    private readonly ChannelMode _channels;
    private readonly float[] _scratch = new float[RaopAudio.FramesPerPacket * 2];
    private long? _nextFrame;

    public RaopTimelineReader(TimelineBuffer timeline, int groupDelayMs, int trimMs, int transportLatencyMs, ChannelMode channels)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (timeline.SampleRate != RaopAudio.SampleRate)
            throw new ArgumentException("RAOP requires a 44100 Hz timeline", nameof(timeline));
        ArgumentOutOfRangeException.ThrowIfNegative(groupDelayMs);
        ArgumentOutOfRangeException.ThrowIfNegative(transportLatencyMs);
        if (!Enum.IsDefined(channels)) throw new ArgumentOutOfRangeException(nameof(channels));
        _timeline = timeline;
        _transportLatencyTicks = MediaClock.FromMs(transportLatencyMs);
        _delayFrames = checked((long)Math.Round(((long)groupDelayMs + trimMs) * RaopAudio.SampleRate / 1000.0));
        _channels = channels;
    }

    public bool Read(long sendDeadlineTicks, Span<short> stereo)
    {
        if (stereo.Length % 2 != 0 || stereo.Length > _scratch.Length)
            throw new ArgumentException("Expected at most 352 complete stereo frames", nameof(stereo));
        int frames = stereo.Length / 2;
        if (frames == 0) return true;
        // RAOP sync presents this packet at send deadline + transport latency. Anchor once: rounding every
        // truncated packet deadline can otherwise repeat or skip a frame (2026-10-15).
        long first = _nextFrame ?? checked(_timeline.FrameAt(checked(sendDeadlineTicks + _transportLatencyTicks)) - _delayFrames);
        long next = checked(first + frames);
        bool complete = _timeline.ReadFrames(first, _scratch.AsSpan(0, stereo.Length));
        for (int frame = 0; frame < frames; frame++)
        {
            short left = Pcm16(_scratch[frame * 2]), right = Pcm16(_scratch[frame * 2 + 1]);
            if (_channels == ChannelMode.LeftOnly) right = left;
            else if (_channels == ChannelMode.RightOnly) left = right;
            stereo[frame * 2] = left;
            stereo[frame * 2 + 1] = right;
        }
        _nextFrame = next;
        return complete;
    }

    private static short Pcm16(float sample)
    {
        if (!float.IsFinite(sample)) return 0;
        float scaled = sample * 32767f;
        return scaled >= 32767f ? short.MaxValue : scaled <= -32768f ? short.MinValue : (short)MathF.Round(scaled);
    }
}
