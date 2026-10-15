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
    int latencyFrames, int groupDelayMs, Func<Action<long>?> onset, IReadOnlyList<int>? trimsMs = null) : IGroupPacketSource
{
    private readonly float[] _pcm = new float[RtpSender.FramesPerPacket * 2];
    private int _quietFrames;
    private long? _nextFrame;
    private long _packetFrame;

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
