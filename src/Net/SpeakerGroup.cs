using HomePodCast.Audio;

namespace HomePodCast.Net;

/// <summary>
/// One speaker's session as a SpeakerGroup drives it (AirPlayClient.PrepareAsync in the app, fakes in
/// tests): pairing, keys, SSRC, NTP timing, event channel and /feedback are all its own, but it has no
/// sender of its own.
/// </summary>
internal interface IGroupMember : IDisposable
{
    string Name { get; }
    RtpTarget Target { get; }
    int LatencyFrames { get; }
    int? ArrivalToRenderMs { get; }
    double? InitialVolumeDb { get; }

    /// <summary>Why the session ended, if it already has (Lost may have fired before anyone listened).</summary>
    string? LostReason { get; }
    event Action<string>? Lost;
    void Record();
    void Flush(ushort firstSeq, uint rtpBase);
    void SetVolumePercent(double percent);
}

/// <summary>How to bring up one member of a group, and its offset from the shared volume (percentage points).</summary>
internal sealed record MemberSetup(string Label, Func<CancellationToken, Task<IGroupMember>> Prepare, int VolumeOffset = 0);

/// <summary>A member of a group failed; Member says which one.</summary>
internal sealed class GroupMemberException(string member, Exception inner)
    : Exception($"{member}: {inner.Message}", inner)
{
    public string Member { get; } = member;
}

/// <summary>
/// Experimental: several AirPlay speakers (a stereo pair, or different speakers for multi-room) on one
/// timeline. Every speaker gets a complete session of its own; one RtpSender sends each packet to all of
/// them back-to-back with the same RTP time and the same sync mapping.
/// All-or-nothing: if any member fails to set up, or is lost later, the group fails as a whole and the
/// caller reconnects every member, so the speakers always restart together on a fresh common timeline.
/// </summary>
internal sealed class SpeakerGroup : IDisposable
{
    private readonly MemberSetup[] _setups;
    private readonly IGroupMember?[] _members;
    private readonly AudioFifo _fifo;
    private readonly TaskCompletionSource<string> _lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? _firstError;
    private int _firstErrorIndex;
    private RtpSender? _sender;
    private int _disposed;

    private SpeakerGroup(IReadOnlyList<MemberSetup> setups, AudioFifo fifo)
    {
        _setups = setups.ToArray();
        _members = new IGroupMember?[_setups.Length];
        _fifo = fifo;
    }

    public RtpSender? Sender => _sender;
    public IReadOnlyList<IGroupMember?> Members => _members;
    public IReadOnlyList<MemberSetup> Setups => _setups;

    /// <summary>Completes (with a display text naming the speaker) as soon as any member's session ends.</summary>
    public Task<string> Lost => _lost.Task;

    /// <summary>The lost member's own reason, e.g. EventChannel.ClosedBySpeaker.</summary>
    public string? LostReason { get; private set; }

    public double MasterVolume { get; private set; }

    public int? ArrivalToRenderMs => _members.Max(m => m?.ArrivalToRenderMs);

    /// <summary>
    /// Set up every member in parallel, RECORD on all of them, then start one shared timeline and FLUSH each
    /// session to it. If any member fails, whatever did connect is torn down and the failure is thrown.
    /// </summary>
    public static async Task<SpeakerGroup> ConnectAsync(IReadOnlyList<MemberSetup> setups, AudioFifo fifo,
        double? volumePercent, CancellationToken ct)
    {
        if (setups.Count == 0) throw new ArgumentException("empty group", nameof(setups));
        var group = new SpeakerGroup(setups, fifo);
        try
        {
            await group.PrepareAllAsync(ct);
            var members = group._members.Select(m => m!).ToArray();

            // RECORD on every speaker first: nothing plays until all of them have accepted the stream.
            await group.EachAsync((m, _) => m.Record(), ct);

            // One timeline: same RTP base and T0 for all; per-session keys, SSRCs and sequence numbers.
            var sender = new RtpSender(members.Select(m => m.Target).ToArray(), members.Max(m => m.LatencyFrames), fifo);
            group._sender = sender;
            sender.Start(MediaClock.Now + MediaClock.FromMs(250));
            await group.EachAsync((m, i) => m.Flush(sender.Streams[i].FirstSeq, sender.RtpBase), ct);

            group.MasterVolume = volumePercent ?? AirPlayClient.DbToPercent(members[0].InitialVolumeDb ?? -20.0);
            await group.EachAsync((m, i) => m.SetVolumePercent(MemberVolume(group.MasterVolume, setups[i].VolumeOffset)), ct);

            Log.Info($"group streaming: {string.Join(" + ", setups.Select((s, i) => $"{s.Label} [{sender.Streams[i].Channels}]"))}, " +
                     $"rtpBase={sender.RtpBase} latency={sender.LatencyFrames} frames");
            return group;
        }
        catch
        {
            group.Dispose();
            throw;
        }
    }

    private async Task PrepareAllAsync(CancellationToken ct)
    {
        using var failFast = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = new Task[_setups.Length];
        for (int i = 0; i < _setups.Length; i++)
        {
            int index = i;
            tasks[i] = Task.Run(async () =>
            {
                try
                {
                    var member = await _setups[index].Prepare(failFast.Token);
                    lock (_members) _members[index] = member;
                    member.Lost += reason => OnMemberLost(index, reason);
                    if (member.LostReason is { } already) OnMemberLost(index, already);
                }
                catch (Exception ex)
                {
                    // The first failure is the reason; the others only get cancelled because of it.
                    lock (_members)
                    {
                        if (_firstError == null) (_firstError, _firstErrorIndex) = (ex, index);
                    }
                    failFast.Cancel();
                }
            });
        }
        await Task.WhenAll(tasks);
        ct.ThrowIfCancellationRequested();
        if (_firstError is { } first) throw new GroupMemberException(_setups[_firstErrorIndex].Label, first);
        ThrowIfLost();
    }

    /// <summary>Run one step on every member at once; a failure fails the group, naming the member.</summary>
    private async Task EachAsync(Action<IGroupMember, int> step, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var tasks = new Task[_members.Length];
        for (int i = 0; i < tasks.Length; i++)
        {
            int index = i;
            tasks[i] = Task.Run(() =>
            {
                try { step(_members[index]!, index); }
                catch (Exception ex) { throw new GroupMemberException(_setups[index].Label, ex); }
            });
        }
        await Task.WhenAll(tasks);
        ThrowIfLost();
    }

    private void OnMemberLost(int index, string reason)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (_lost)
        {
            if (_lost.Task.IsCompleted) return;
            LostReason = reason;
            Log.Warn($"group: {_setups[index].Label} lost ({reason}); reconnecting all speakers");
            _lost.TrySetResult(L.F("{0}：{1}", _setups[index].Label, reason));
        }
    }

    private void ThrowIfLost()
    {
        if (_lost.Task.IsCompleted) throw new AirPlayException(_lost.Task.Result);
    }

    /// <summary>Linked volume: each speaker gets the shared volume plus its own offset.</summary>
    public void SetVolumePercent(double master)
    {
        MasterVolume = master;
        for (int i = 0; i < _members.Length; i++)
        {
            if (_members[i] is not { } m) continue;
            try { m.SetVolumePercent(MemberVolume(master, _setups[i].VolumeOffset)); }
            catch (Exception ex) { Log.Warn($"group volume, {_setups[i].Label}: {ex.Message}"); }
        }
    }

    /// <summary>Shared volume + offset, clamped to 0..100; a muted (0) group stays muted everywhere.</summary>
    internal static double MemberVolume(double master, int offset) =>
        master <= 0 ? 0 : Math.Clamp(master + offset, 0, 100);

    public void LogStats()
    {
        if (_sender is not { } s) return;
        var legs = string.Join(" | ", s.Streams.Select((st, i) =>
            $"{_setups[i].Label}: sent={st.PacketsSent} rtx={st.Retransmitted}/{st.RetransmitRequests} rtxMiss={st.RetransmitMisses}"));
        Log.Info($"group stats: fifo={_fifo.Depth * 1000.0 / RtpSender.SampleRate:F0}ms underruns={_fifo.Underruns} " +
                 $"overflows={_fifo.Overflows} sent={s.PacketsSent} late={s.LateWakeups} maxLate={s.MaxLateMs:F1}ms " +
                 $"skipped={s.SkippedPackets} | {legs}");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _sender?.Dispose(); // stop the shared timeline first, then end every session
        IGroupMember?[] members;
        lock (_members) members = _members.ToArray();
        var teardowns = members.Where(m => m != null).Select(m => Task.Run(m!.Dispose)).ToArray();
        try { Task.WaitAll(teardowns); } catch (AggregateException ex) { Log.Warn($"group teardown: {ex.InnerException?.Message}"); }
    }
}
