using HomePodCast.Net;

namespace HomePodCast.Audio;

/// <summary>One paced FIFO/effects producer for mixed groups; every transport reads the resulting common history.</summary>
internal sealed class TimelinePump(AudioFifo fifo, IAudioEffect? effects, int historyMs, long? originTicks = null) : IDisposable
{
    public const int HeadroomMs = 20;
    private Thread? _thread;
    private volatile bool _stop;
    private readonly float[] _pcm = new float[RtpSender.FramesPerPacket * 2];
    private long? _nextFrame;
    public TimelineBuffer Timeline { get; } = new(RtpSender.SampleRate,
        checked(RtpSender.SampleRate * Math.Max(5000, historyMs) / 1000), originTicks ?? MediaClock.Now);
    public event Action<string>? Failed;
    public long SilentPackets { get; private set; }
    public long SkippedPackets { get; private set; }
    public long ProducedPackets { get; private set; }
    public bool IsRunning => _thread?.IsAlive == true;

    internal void Produce(long ticks)
    {
        if (!fifo.Read(_pcm)) SilentPackets++;
        effects?.Process(_pcm, RtpSender.FramesPerPacket);
        // Anchor once: rounding each truncated QPC packet deadline can otherwise make one 351-frame step.
        long frame = _nextFrame ?? Timeline.FrameAt(ticks);
        Timeline.WriteFrames(frame, _pcm);
        _nextFrame = checked(frame + RtpSender.FramesPerPacket);
        ProducedPackets++;
    }

    public void Start(long t0)
    {
        if (_thread != null || _nextFrame != null) throw new InvalidOperationException("Timeline producer already started");
        _nextFrame = Timeline.FrameAt(t0);
        _thread = new Thread(() => Run(t0)) { IsBackground = true, Name = "Group audio timeline", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    private void Run(long t0)
    {
        using var mmcss = Native.EnterMmcss("Pro Audio");
        using var timer = new Native.PreciseTimer();
        try
        {
            long packet = 0;
            while (!_stop)
            {
                long deadline = t0 + (long)(packet * (double)RtpSender.FramesPerPacket * MediaClock.Frequency / RtpSender.SampleRate);
                long late = MediaClock.Now - deadline;
                if (late < 0) { timer.Sleep(Math.Min(-late, MediaClock.FromMs(50))); continue; }
                if (late > MediaClock.FromMs(60))
                {
                    long skip = (long)(late * (double)RtpSender.SampleRate / RtpSender.FramesPerPacket / MediaClock.Frequency);
                    packet += skip; SkipPackets(skip);
                    fifo.DiscardToTarget();
                    continue;
                }
                Produce(deadline);
                packet++;
            }
        }
        catch (Exception ex) when (!_stop) { Failed?.Invoke(ex.Message); }
        catch (Exception) { } // stopped while a source or the native timer was failing
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread != null && Thread.CurrentThread != _thread) _thread.Join(1000);
    }

    internal void SkipPackets(long packets)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(packets);
        if (_nextFrame is not { } frame) throw new InvalidOperationException("Timeline has no frame anchor");
        _nextFrame = checked(frame + packets * RtpSender.FramesPerPacket);
        SkippedPackets += packets;
    }
}

/// <summary>Adapts a shared source history to the unchanged group RTP sender's transport deadlines.</summary>
internal sealed class TimelineGroupSource(TimelineBuffer timeline, IReadOnlyList<ChannelMode> channels,
    int latencyFrames, int groupDelayMs, Func<Action<long>?> onset, IReadOnlyList<int>? trimsMs = null,
    IReadOnlyList<string>? targetIds = null, IReadOnlyList<string>? localTargetIds = null) : IGroupPacketSource, IGroupAcousticCalibration
{
    private static readonly float[] CalibrationChirp = AcousticAlignment.Chirp();
    private readonly float[] _pcm = new float[RtpSender.FramesPerPacket * 2];
    private int _quietFrames;
    private int _acousticCalibrationActive;
    private long? _nextFrame;
    private long _packetFrame;
    private AcousticPulse? _acousticPulse;
    private sealed record AcousticPulse(string TargetId, long StartTicks);
    public bool CanCalibrateAcoustically => targetIds != null && targetIds.Count == channels.Count
        && targetIds.Count + (localTargetIds?.Count ?? 0) >= 2;

    public long BeginAcousticPulse(string targetId)
    {
        if (!CanCalibrateAcoustically) throw new InvalidOperationException("This active group cannot be microphone-calibrated");
        bool known = targetIds!.Contains(targetId, StringComparer.OrdinalIgnoreCase)
            || (localTargetIds?.Contains(targetId, StringComparer.OrdinalIgnoreCase) ?? false);
        if (!known) throw new InvalidOperationException("The selected output is not in the active group");
        long start = MediaClock.Now + MediaClock.FromMs(1200);
        Volatile.Write(ref _acousticPulse, new(targetId, start));
        return start;
    }

    public bool RenderCalibrationPulse(string targetId, long firstFrameTicks, int sampleRate, int trimMs, Span<float> stereo)
    {
        if (Volatile.Read(ref _acousticCalibrationActive) == 0) return false;
        stereo.Clear();
        if (Volatile.Read(ref _acousticPulse) is not { } pulse) return true;
        if (!string.Equals(targetId, pulse.TargetId, StringComparison.OrdinalIgnoreCase)) return true;
        for (int frame = 0; frame < stereo.Length / 2; frame++)
        {
            long at = firstFrameTicks + (long)((Int128)frame * MediaClock.Frequency / sampleRate);
            long sample = (long)Math.Round((at - pulse.StartTicks - MediaClock.FromMs(trimMs)) * (double)timeline.SampleRate / MediaClock.Frequency);
            if ((ulong)sample < (ulong)CalibrationChirp.Length)
                stereo[frame * 2] = stereo[frame * 2 + 1] = CalibrationChirp[(int)sample];
        }
        return true;
    }

    public void EndAcousticCalibration() => Volatile.Write(ref _acousticPulse, null);
    public void SetAcousticCalibrationActive(bool active) => Volatile.Write(ref _acousticCalibrationActive, active ? 1 : 0);

    public bool ReadPacket(long deadline)
    {
        long source = deadline + (long)(latencyFrames * (double)MediaClock.Frequency / RtpSender.SampleRate) - MediaClock.FromMs(groupDelayMs);
        // A half-frame delay offset plus truncated QPC packet deadlines can otherwise alternate 351/353 steps.
        _packetFrame = _nextFrame ?? timeline.FrameAt(source);
        _nextFrame = checked(_packetFrame + RtpSender.FramesPerPacket);
        bool complete = timeline.ReadFrames(_packetFrame, _pcm);
        if (onset() is { } report)
        {
            for (int i = 0; i < RtpSender.FramesPerPacket; i++)
            {
                if (Math.Abs(_pcm[i * 2]) > 0.02f)
                {
                    if (_quietFrames > RtpSender.SampleRate * 3 / 10)
                        report(deadline + (long)(i * (double)MediaClock.Frequency / RtpSender.SampleRate));
                    _quietFrames = 0;
                }
                else _quietFrames++;
            }
        }
        return complete;
    }

    public void ReadTarget(int index, Span<float> stereo)
    {
        if (Volatile.Read(ref _acousticCalibrationActive) != 0 && Volatile.Read(ref _acousticPulse) is null)
        { stereo.Clear(); return; }
        if (Volatile.Read(ref _acousticPulse) is { } pulse)
        {
            stereo.Clear();
            if (string.Equals(targetIds![index], pulse.TargetId, StringComparison.OrdinalIgnoreCase))
            {
                long firstTicks = timeline.TicksAt(_packetFrame) + MediaClock.FromMs(groupDelayMs);
                for (int frame = 0; frame < stereo.Length / 2; frame++)
                {
                    long at = firstTicks + (long)((Int128)frame * MediaClock.Frequency / timeline.SampleRate);
                    long sample = (long)Math.Round((at - pulse.StartTicks - MediaClock.FromMs(trimsMs?[index] ?? 0))
                        * (double)timeline.SampleRate / MediaClock.Frequency);
                    if ((ulong)sample < (ulong)CalibrationChirp.Length)
                        stereo[frame * 2] = stereo[frame * 2 + 1] = CalibrationChirp[(int)sample];
                }
            }
            return;
        }
        int trim = trimsMs?[index] ?? 0;
        if (trim == 0) _pcm.CopyTo(stereo);
        else timeline.ReadFrames(_packetFrame - (long)Math.Round(trim * RtpSender.SampleRate / 1000.0), stereo);
        if (channels[index] == ChannelMode.Stereo) return;
        int channel = channels[index] == ChannelMode.LeftOnly ? 0 : 1;
        for (int i = 0; i < stereo.Length / 2; i++) stereo[i * 2] = stereo[i * 2 + 1] = stereo[i * 2 + channel];
    }
    public void SkipPackets(int packets)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(packets);
        if (_nextFrame is { } frame) _nextFrame = checked(frame + packets * (long)RtpSender.FramesPerPacket);
    }
    public void Dispose() { }
}
