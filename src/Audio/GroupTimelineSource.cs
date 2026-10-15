using HomePodCast.Net;

namespace HomePodCast.Audio;

/// <summary>
/// Adapts the existing group's one FIFO/effects read to a shared history. RtpSender still owns the deadlines,
/// stalls and wire format; the single-speaker sender and the discrete cinema source stay unchanged.
/// Timestamps are the group's source-read deadlines, after the FIFO's existing adaptive capture buffering.
/// </summary>
internal sealed class GroupTimelineSource(AudioFifo fifo, IReadOnlyList<ChannelMode> channels, IAudioEffect? effects,
    Func<Action<long>?> onset) : IGroupPacketSource
{
    private readonly float[] _pcm = new float[RtpSender.FramesPerPacket * 2];
    private long _packetFrame;
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
        Timeline.Write(sourceTicks, _pcm);
        _packetFrame = Timeline.FrameAt(sourceTicks);
        return complete;
    }

    public void ReadTarget(int index, Span<float> stereo)
    {
        Timeline.ReadFrames(_packetFrame, stereo);
        var mode = channels[index];
        if (mode == ChannelMode.Stereo) return;
        int channel = mode == ChannelMode.LeftOnly ? 0 : 1;
        for (int i = 0; i < stereo.Length / 2; i++)
            stereo[i * 2] = stereo[i * 2 + 1] = stereo[i * 2 + channel];
    }

    // The sender already discards the FIFO to its target on a stall. Its next absolute deadline leaves a gap here.
    public void SkipPackets(int packets) { }
    public void Dispose() { }
}
