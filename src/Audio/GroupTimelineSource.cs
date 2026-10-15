using HomePodCast.Net;

namespace HomePodCast.Audio;

/// <summary>
/// Adapts the existing group's one FIFO/effects read to a shared history. RtpSender still owns the deadlines,
/// stalls and wire format; the single-speaker sender and the discrete cinema source stay unchanged.
/// Timestamps are the group's source-read deadlines, after the FIFO's existing adaptive capture buffering.
/// </summary>
internal sealed class GroupTimelineSource(AudioFifo fifo, IReadOnlyList<ChannelMode> channels, IAudioEffect? effects,
    Func<Action<long>?> onset, IReadOnlyList<int>? trimsMs = null) : IGroupPacketSource
{
    private readonly float[] _pcm = new float[RtpSender.FramesPerPacket * 2];
    private long _packetFrame;
    private long? _nextFrame;
    private int _quietFrames;
    public TimelineBuffer Timeline { get; } = new(RtpSender.SampleRate, RtpSender.SampleRate * 5, MediaClock.Now);

    public bool ReadPacket(long sourceTicks)
    {
        bool complete = fifo.Read(_pcm);
        effects?.Process(_pcm, RtpSender.FramesPerPacket);
        if (onset() is { } report)
        {
            // Same onset definition as the unchanged stereo path in RtpSender.
            for (int i = 0; i < RtpSender.FramesPerPacket; i++)
            {
                if (Math.Abs(_pcm[i * 2]) > 0.02f)
                {
                    if (_quietFrames > RtpSender.SampleRate * 3 / 10)
                        report(sourceTicks + (long)(i * (double)MediaClock.Frequency / RtpSender.SampleRate));
                    _quietFrames = 0;
                }
                else _quietFrames++;
            }
        }
        // Anchor once. Truncating each QPC deadline and then rounding it to a frame can otherwise produce
        // a 351-frame step for a 352-frame packet, overlapping the previous write (2026-10-15).
        _packetFrame = _nextFrame ?? Timeline.FrameAt(sourceTicks);
        Timeline.WriteFrames(_packetFrame, _pcm);
        _nextFrame = checked(_packetFrame + RtpSender.FramesPerPacket);
        return complete;
    }

    public void ReadTarget(int index, Span<float> stereo)
    {
        long trimFrames = (long)Math.Round((trimsMs?[index] ?? 0) * RtpSender.SampleRate / 1000.0);
        Timeline.ReadFrames(_packetFrame - trimFrames, stereo);
        var mode = channels[index];
        if (mode == ChannelMode.Stereo) return;
        int channel = mode == ChannelMode.LeftOnly ? 0 : 1;
        for (int i = 0; i < stereo.Length / 2; i++)
            stereo[i * 2] = stereo[i * 2 + 1] = stereo[i * 2 + channel];
    }

    // The sender already discards the FIFO on a stall; leave the same whole-packet gap in the shared history.
    public void SkipPackets(int packets)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(packets);
        if (_nextFrame is { } frame) _nextFrame = checked(frame + packets * (long)RtpSender.FramesPerPacket);
    }
    public void Dispose() { }
}
