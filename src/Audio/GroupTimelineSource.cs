using HomePodCast.Net;

namespace HomePodCast.Audio;

/// <summary>
/// Adapts the existing group's one FIFO/effects read to a shared history. RtpSender still owns the deadlines,
/// stalls and wire format; the single-speaker sender and the discrete cinema source stay unchanged.
/// Timestamps are the group's source-read deadlines, after the FIFO's existing adaptive capture buffering.
/// </summary>
internal sealed class GroupTimelineSource(AudioFifo fifo, IReadOnlyList<ChannelMode> channels, IAudioEffect? effects,
    Func<Action<long>?> onset, IReadOnlyList<int>? trimsMs = null, IReadOnlyList<string>? targetIds = null,
    int latencyFrames = 0) : IGroupPacketSource
{
    private static readonly float[] CalibrationChirp = AcousticAlignment.Chirp();
    private readonly float[] _pcm = new float[RtpSender.FramesPerPacket * 2];
    private long _packetFrame;
    private long? _nextFrame;
    private int _quietFrames;
    private AcousticPulse? _acousticPulse;
    private sealed record AcousticPulse(int Target, long StartTicks);
    public TimelineBuffer Timeline { get; } = new(RtpSender.SampleRate, RtpSender.SampleRate * 5, MediaClock.Now);
    internal IReadOnlyList<string> CalibrationTargetIds { get; } = targetIds ?? [];
    internal bool CanCalibrateAcoustically => CalibrationTargetIds.Count >= 2 && CalibrationTargetIds.Count == channels.Count;

    internal long BeginAcousticPulse(string targetId)
    {
        int target = -1;
        for (int i = 0; i < CalibrationTargetIds.Count; i++)
            if (string.Equals(CalibrationTargetIds[i], targetId, StringComparison.OrdinalIgnoreCase)) { target = i; break; }
        if (target < 0 || !CanCalibrateAcoustically) throw new InvalidOperationException("The selected live group cannot be microphone-calibrated");
        long frame = Timeline.FrameAt(MediaClock.Now + MediaClock.FromMs(1200));
        long start = Timeline.TicksAt(frame) + (long)((Int128)latencyFrames * MediaClock.Frequency / Timeline.SampleRate);
        Volatile.Write(ref _acousticPulse, new(target, start));
        return start;
    }

    internal void EndAcousticCalibration() => Volatile.Write(ref _acousticPulse, null);

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
        if (Volatile.Read(ref _acousticPulse) is { } pulse)
        {
            stereo.Clear();
            if (index == pulse.Target)
            {
                long firstTicks = Timeline.TicksAt(_packetFrame)
                    + (long)((Int128)latencyFrames * MediaClock.Frequency / Timeline.SampleRate);
                for (int frame = 0; frame < stereo.Length / 2; frame++)
                {
                    long at = firstTicks + (long)((Int128)frame * MediaClock.Frequency / Timeline.SampleRate);
                    long sample = (long)Math.Round((at - pulse.StartTicks) * (double)Timeline.SampleRate / MediaClock.Frequency);
                    if ((ulong)sample < (ulong)CalibrationChirp.Length)
                        stereo[frame * 2] = stereo[frame * 2 + 1] = CalibrationChirp[(int)sample];
                }
            }
            return;
        }
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
