using HomePodCast.Net;

namespace HomePodCast.Audio;

/// <summary>One independently clocked DLNA cursor over the already processed, shared 44100 Hz stereo history.</summary>
internal sealed class DlnaTimelineReader
{
    public const int SampleRate = 44100, MaximumFrames = 352;
    private readonly TimelineBuffer _timeline;
    private readonly long _latencyTicks, _delayFrames;
    private readonly ChannelMode _channels;
    private readonly ClockedTimelineReader _sinc;
    private readonly float[] _scratch = new float[MaximumFrames * 2];
    private double? _position;

    public double Position => _position ?? double.NaN;

    public DlnaTimelineReader(TimelineBuffer timeline, int groupDelayMs, int trimMs, int estimatedReceiverLatencyMs,
        ChannelMode channels)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (timeline.SampleRate != SampleRate)
            throw new ArgumentException("DLNA requires a 44100 Hz timeline", nameof(timeline));
        ArgumentOutOfRangeException.ThrowIfNegative(groupDelayMs);
        ArgumentOutOfRangeException.ThrowIfNegative(estimatedReceiverLatencyMs);
        if (!Enum.IsDefined(channels)) throw new ArgumentOutOfRangeException(nameof(channels));
        _timeline = timeline;
        _delayFrames = checked((long)Math.Round(((long)groupDelayMs + trimMs) * SampleRate / 1000.0));
        _latencyTicks = MediaClock.FromMs(estimatedReceiverLatencyMs);
        _channels = channels;
        _sinc = new ClockedTimelineReader(timeline, SampleRate, MaximumFrames);
    }

    /// <summary>A new HTTP stream or a pause starts a new presentation anchor.</summary>
    public void Reset() => _position = null;

    public bool Read(long sendTicks, double sourceFramesPerOutputFrame, Span<short> stereo)
    {
        if (stereo.Length % 2 != 0 || stereo.Length > _scratch.Length)
            throw new ArgumentException("Expected at most 352 complete stereo frames", nameof(stereo));
        if (!double.IsFinite(sourceFramesPerOutputFrame)
            || sourceFramesPerOutputFrame < 1 / (1 + DlnaReadClock.MaximumDriftPpm / 1e6)
            || sourceFramesPerOutputFrame > 1 / (1 - DlnaReadClock.MaximumDriftPpm / 1e6))
            throw new ArgumentOutOfRangeException(nameof(sourceFramesPerOutputFrame));
        int frames = stereo.Length / 2;
        if (frames == 0) return true;
        // Anchor only once. Socket scheduling jitter changes neither the source cursor nor the group delay.
        double position = _position ?? checked(_timeline.FrameAt(checked(sendTicks + _latencyTicks)) - _delayFrames);
        bool complete;
        Span<float> samples = _scratch.AsSpan(0, stereo.Length);
        if (sourceFramesPerOutputFrame == 1 && position == Math.Truncate(position))
        {
            // Preserve PCM sample identity until rate correction is observable and needed.
            complete = _timeline.ReadFrames(checked((long)position), samples);
        }
        else
        {
            // Reuse the centered 64-tap/128-phase filter. Project the carried fractional cursor, not this call's
            // send deadline; MediaClock rounding is far below one sample and never accumulates in Position.
            long presentation = checked(_timeline.OriginTicks
                + (long)Math.Round(position * MediaClock.Frequency / SampleRate));
            complete = _sinc.Read(presentation, 0, 0, SampleRate / sourceFramesPerOutputFrame, samples);
        }
        for (int frame = 0; frame < frames; frame++)
        {
            short left = Pcm16(samples[frame * 2]), right = Pcm16(samples[frame * 2 + 1]);
            if (_channels == ChannelMode.LeftOnly) right = left;
            else if (_channels == ChannelMode.RightOnly) left = right;
            stereo[frame * 2] = left;
            stereo[frame * 2 + 1] = right;
        }
        _position = position + frames * sourceFramesPerOutputFrame;
        return complete;
    }

    private static short Pcm16(float sample)
    {
        if (!float.IsFinite(sample)) return 0;
        float scaled = sample * 32767f;
        return scaled >= 32767f ? short.MaxValue : scaled <= -32768f ? short.MinValue : (short)MathF.Round(scaled);
    }
}
