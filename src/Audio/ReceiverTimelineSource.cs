using HomePodCast.Net;

namespace HomePodCast.Audio;

internal delegate bool ReceiverPcmReader(long sendTicks, Span<float> stereo);
internal interface IReceiverAudioSource : IGroupPacketSource
{
    bool Read(long sendTicks, Span<float> stereo);
}

/// <summary>Third-party 352/1024-frame transports read processed history on their own presentation clock.</summary>
internal sealed class ReceiverTimelineSource : IReceiverAudioSource
{
    private readonly TimelineBuffer _timeline;
    private readonly long _latencyTicks, _delayFrames;
    private readonly ChannelMode _channels;
    private readonly float[] _packet = new float[RtpSender.FramesPerPacket * 2];
    private long? _nextFrame;
    public ReceiverTimelineSource(TimelineBuffer timeline, int groupDelayMs, int trimMs, int transportLatencyMs, ChannelMode channels)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (timeline.SampleRate != 44100) throw new ArgumentException("AirPlay requires a 44100 Hz timeline", nameof(timeline));
        ArgumentOutOfRangeException.ThrowIfNegative(groupDelayMs);
        ArgumentOutOfRangeException.ThrowIfNegative(transportLatencyMs);
        if (!Enum.IsDefined(channels)) throw new ArgumentOutOfRangeException(nameof(channels));
        _timeline = timeline; _latencyTicks = MediaClock.FromMs(transportLatencyMs); _channels = channels;
        _delayFrames = checked((long)Math.Round(((long)groupDelayMs + trimMs) * 44100 / 1000.0));
    }

    public bool Read(long sendTicks, Span<float> stereo)
    {
        if (stereo.Length % 2 != 0 || stereo.Length > 2048) throw new ArgumentException("Expected at most 1024 stereo frames", nameof(stereo));
        if (stereo.Length == 0) return true;
        long first = _nextFrame ?? checked(_timeline.FrameAt(checked(sendTicks + _latencyTicks)) - _delayFrames);
        bool complete = _timeline.ReadFrames(first, stereo);
        _nextFrame = checked(first + stereo.Length / 2);
        for (int frame = 0; frame < stereo.Length / 2; frame++)
        {
            float left = float.IsFinite(stereo[frame * 2]) ? stereo[frame * 2] : 0;
            float right = float.IsFinite(stereo[frame * 2 + 1]) ? stereo[frame * 2 + 1] : 0;
            if (_channels == ChannelMode.LeftOnly) right = left;
            else if (_channels == ChannelMode.RightOnly) left = right;
            stereo[frame * 2] = left; stereo[frame * 2 + 1] = right;
        }
        return complete;
    }

    public bool ReadPacket(long sourceTicks) => Read(sourceTicks, _packet);
    public void ReadTarget(int index, Span<float> stereo)
    {
        if (index != 0 || stereo.Length != _packet.Length) throw new ArgumentOutOfRangeException(nameof(index));
        _packet.CopyTo(stereo);
    }
    public void SkipPackets(int packets)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(packets);
        if (_nextFrame is { } frame) _nextFrame = checked(frame + (long)packets * RtpSender.FramesPerPacket);
    }
    public void Dispose() { }
}
