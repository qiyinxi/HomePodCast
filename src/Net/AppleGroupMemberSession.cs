using HomePodCast.Audio;

namespace HomePodCast.Net;

/// <summary>An independent Apple realtime leg; the group owns the processed source history.</summary>
internal sealed class AppleGroupMemberSession : IPreparedReceiverSession
{
    private readonly IGroupMember _member;
    private readonly TaskCompletionSource<string> _lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _lifecycle = new();
    private Exception? _failure;
    private RtpSender? _sender;
    private bool _stopped, _disposed;
    public ReceiverProtocol Protocol => ReceiverProtocol.AirPlay2Realtime;
    public bool UsesPtp => false;
    public int LatencyMs => (int)Math.Round(_member.LatencyFrames * 1000.0 / RtpSender.SampleRate);
    public int? ArrivalToRenderMs => _member.ArrivalToRenderMs;
    public IReadOnlyDictionary<string, object?> Info => (_member as AirPlayClient)?.Info ?? new Dictionary<string, object?>();
    public Task<string> Lost => _lost.Task;
    public Exception? LostException => Volatile.Read(ref _failure);
    public long PacketsSent => _sender?.PacketsSent ?? 0;
    internal RtpSender? Sender => _sender;
    public AppleGroupMemberSession(IGroupMember member)
    {
        _member = member;
        // Subscribe first: an orderly event-channel close between these two operations must stay terminal.
        member.Lost += OnLost;
        if (member.LostReason is { } reason) OnLost(reason);
    }
    private void OnLost(string reason)
    {
        Exception error = reason == EventChannel.ClosedBySpeaker ? new ReceiverGroupStoppedException(reason) : new IOException(reason);
        Interlocked.CompareExchange(ref _failure, error, null); _lost.TrySetResult(reason);
    }
    public void Start(IReceiverAudioSource source, long startAt, double percent, double cap)
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopped || _sender != null) throw new InvalidOperationException("Apple group output already stopped or started");
            if (Lost.IsCompleted) throw LostException!;
            SetVolume(percent, cap);
            _member.Record();
            // ReceiverTimelineSource already applied the channel map and trim. A private empty FIFO ensures
            // sender stall recovery cannot discard the group's capture, and no effects run a second time.
            var sender = new RtpSender([_member.Target with { Channels = ChannelMode.Stereo }], _member.LatencyFrames,
                new AudioFifo(RtpSender.SampleRate, 0, 0)) { GroupSource = source };
            _sender = sender;
            try { sender.Start(startAt); _member.Flush(sender.FirstSeq, sender.RtpBase); }
            catch { sender.Dispose(); _sender = null; throw; }
        }
    }
    public void SetVolume(double percent, double cap)
    {
        lock (_lifecycle)
        {
            if (_disposed || _stopped) return;
            if (_member is AirPlayClient client)
            {
                client.VolumeCapPercent = cap;
                client.SetGroupVolumePercentConfirmed(VolumeLimit.Clamp(percent, cap));
            }
            else _member.SetVolumePercent(VolumeLimit.Clamp(percent, cap));
        }
    }
    public void StopSending()
    {
        lock (_lifecycle) { _stopped = true; Interlocked.Exchange(ref _sender, null)?.Dispose(); }
    }
    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed) return; _disposed = true;
            StopSending();
            _member.Lost -= OnLost; _member.Dispose();
        }
    }
}
