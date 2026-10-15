using HomePodCast.Audio;

namespace HomePodCast.Net;

/// <summary>How to bring up one member of a group, and its offset from the shared volume (percentage points).</summary>
internal sealed record MemberSetup(string Label, Func<CancellationToken, Task<IGroupMember>> Prepare, int VolumeOffset = 0,
    string? Id = null, int DelayTrimMs = 0);

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
internal sealed class SpeakerGroup : IOutputSink
{
    private readonly MemberSetup[] _setups;
    private readonly IGroupMember?[] _members;
    private readonly AudioFifo _fifo;
    private readonly TaskCompletionSource<string> _lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _lostForLog;
    private Exception? _firstError;
    private int _firstErrorIndex;
    private RtpSender? _sender;
    private int _disposed;
    private readonly Func<double> _volumeCap;
    private LocalOutputSet? _localOutputs;
    private TimelinePump? _pump;
    private CancellationTokenSource? _outputStop;

    private SpeakerGroup(IReadOnlyList<MemberSetup> setups, AudioFifo fifo, Func<double>? volumeCap)
    {
        _setups = setups.ToArray();
        _members = new IGroupMember?[_setups.Length];
        _fifo = fifo;
        _volumeCap = volumeCap ?? (() => 100);
    }

    /// <summary>The user's ceiling (percent), read live: no member, offset included, is ever sent more.</summary>
    public double VolumeCapPercent => _volumeCap();

    public RtpSender? Sender => _sender;
    public IReadOnlyList<IGroupMember?> Members => _members;
    public IReadOnlyList<MemberSetup> Setups => _setups;

    /// <summary>Completes (with a display text naming the speaker) as soon as any member's session ends.</summary>
    public Task<string> Lost => _lost.Task;

    /// <summary>The lost member's own reason, e.g. EventChannel.ClosedBySpeaker.</summary>
    public string? LostReason { get; private set; }

    public double MasterVolume { get; private set; }

    public OutputKind Kind => OutputKind.AirPlay2;
    public string Id => string.Join("|", _setups.Select(s => s.Id ?? s.Label));
    public string Name => string.Join(" + ", _setups.Select(s => s.Label));
    public int MinLatencyMs => (int)Math.Ceiling((_sender?.LatencyFrames ?? 0) * 1000.0 / RtpSender.SampleRate);
    public OutputState State => Volatile.Read(ref _disposed) != 0 ? OutputState.Stopped
        : _lost.Task.IsCompleted ? OutputState.Lost : OutputState.Streaming;
    public OutputStats Stats => new(_sender?.PacketsSent ?? 0, _sender?.RetransmitRequests ?? 0,
        _sender?.Retransmitted ?? 0, _sender?.RetransmitMisses ?? 0);
    public TimelineBuffer? Timeline { get; private set; }
    public IReadOnlyList<LocalOutputStatus> LocalOutputs => _localOutputs?.Status ?? [];

    public void SetVolume(double percent, bool mute = false) => SetVolumePercent(mute ? 0 : percent);

    public int? ArrivalToRenderMs => _members.Max(m => m?.ArrivalToRenderMs);

    /// <summary>
    /// Set up every member in parallel, RECORD on all of them, then start one shared timeline and FLUSH each
    /// session to it. If any member fails, whatever did connect is torn down and the failure is thrown.
    /// </summary>
    public static async Task<SpeakerGroup> ConnectAsync(IReadOnlyList<MemberSetup> setups, AudioFifo fifo,
        double? volumePercent, CancellationToken ct, IAudioEffect? effects = null, Func<double>? volumeCap = null,
        IGroupPacketSource? source = null, LocalOutputSet? localOutputs = null, int? groupDelayMs = null)
    {
        if (setups.Count == 0) throw new ArgumentException("empty group", nameof(setups));
        if (setups.Any(s => s.DelayTrimMs is < 0 or > 500)) throw new ArgumentOutOfRangeException(nameof(setups), "Speaker delay must be 0..500 ms");
        var group = new SpeakerGroup(setups, fifo, volumeCap);
        group._localOutputs = localOutputs;
        try
        {
            if (localOutputs != null && (source != null || groupDelayMs is null))
                throw new ArgumentException("Local outputs require a coordinated capture timeline and group delay");
            await group.PrepareAllAsync(ct);
            var members = group._members.Select(m => m!).ToArray();

            // RECORD on every speaker first: nothing plays until all of them have accepted the stream.
            await group.EachAsync((m, _) => m.Record(), ct);

            // One timeline: same RTP base and T0 for all; per-session keys, SSRCs and sequence numbers.
            long? coordinatedT0 = null;
            if (localOutputs != null)
            {
                coordinatedT0 = MediaClock.Now + MediaClock.FromMs(250);
                var pump = new TimelinePump(fifo, effects, localOutputs.HistoryMs(groupDelayMs!.Value), coordinatedT0);
                group._pump = pump;
                group.Timeline = pump.Timeline;
                pump.Failed += reason => group.OnOutputLost(group.Name, reason);
                source = new TimelineGroupSource(pump.Timeline, members.Select(m => m.Target.Channels).ToArray(),
                    members.Max(m => m.LatencyFrames), groupDelayMs.Value, () => group._sender?.OnsetSent,
                    setups.Select(s => s.DelayTrimMs).ToArray());
            }
            else if (source == null)
            {
                var timelineSource = new GroupTimelineSource(fifo, members.Select(m => m.Target.Channels).ToArray(),
                    effects, () => group._sender?.OnsetSent, setups.Select(s => s.DelayTrimMs).ToArray());
                source = timelineSource;
                group.Timeline = timelineSource.Timeline;
            }
            // RtpSender's legacy stall handler discards its FIFO. In a mixed group that FIFO belongs to the
            // producer, so give this history-only consumer a private empty one; a stalled AirPlay leg cannot
            // drop captured audio that the healthy local outputs still need. The original sender stays unchanged.
            var senderFifo = localOutputs == null ? fifo : new AudioFifo(RtpSender.SampleRate, 0, 0);
            var sender = new RtpSender(members.Select(m => m.Target).ToArray(), members.Max(m => m.LatencyFrames), senderFifo)
            {
                Effects = effects, // one chain, run once per packet before the per-speaker encode
                GroupSource = source,
            };
            group._sender = sender;
            long t0 = coordinatedT0 ?? MediaClock.Now + MediaClock.FromMs(250);
            group._pump?.Start(t0);
            sender.Start(t0 + (localOutputs == null ? 0 : MediaClock.FromMs(TimelinePump.HeadroomMs)));
            await group.EachAsync((m, i) => m.Flush(sender.Streams[i].FirstSeq, sender.RtpBase), ct);

            group.MasterVolume = VolumeLimit.Clamp(
                volumePercent ?? AirPlayClient.DbToPercent(members[0].InitialVolumeDb ?? -20.0), group.VolumeCapPercent);
            await group.EachAsync((m, i) => m.SetVolumePercent(
                MemberVolume(group.MasterVolume, setups[i].VolumeOffset, group.VolumeCapPercent)), ct);

            if (localOutputs != null)
            {
                await localOutputs.StartAsync(group.Timeline!, groupDelayMs!.Value, group.MasterVolume, ct);
                group._outputStop = new CancellationTokenSource();
                _ = group.WatchLocalOutputs(localOutputs, group._outputStop.Token);
                group.ThrowIfLost();
            }

            Log.Info($"group streaming: {string.Join(" + ", setups.Select((s, i) => $"{MemberForLog(s, i)} [{sender.Streams[i].Channels}]"))}, " +
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
            _lostForLog = $"{MemberForLog(_setups[index], index)} lost ({reason})";
            Log.Warn($"group: {_lostForLog}; reconnecting all speakers");
            _lost.TrySetResult(L.F("{0}：{1}", _setups[index].Label, reason));
        }
    }

    private void ThrowIfLost()
    {
        if (_lost.Task.IsCompleted) throw new AirPlayException(_lost.Task.Result) { LogMessage = _lostForLog };
    }

    private async Task WatchLocalOutputs(LocalOutputSet outputs, CancellationToken ct)
    {
        try
        {
            await outputs.Lost.WaitAsync(ct);
            OnOutputLost(outputs.LostOutputName ?? Name, outputs.LostReason ?? "local output lost");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private void OnOutputLost(string name, string reason)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (_lost)
        {
            if (_lost.Task.IsCompleted) return;
            LostReason = reason;
            _lostForLog = $"{Log.Name(name)} lost ({reason})";
            Log.Warn($"group: {_lostForLog}; reconnecting required outputs");
            _lost.TrySetResult(L.F("{0}：{1}", name, reason));
        }
    }

    /// <summary>Linked volume: each speaker gets the shared volume plus its own offset, never above the cap.</summary>
    public void SetVolumePercent(double master)
    {
        MasterVolume = master;
        _localOutputs?.SetVolume(master);
        double cap = VolumeCapPercent;
        for (int i = 0; i < _members.Length; i++)
        {
            if (_members[i] is not { } m) continue;
            try { m.SetVolumePercent(MemberVolume(master, _setups[i].VolumeOffset, cap)); }
            catch (Exception ex) { Log.Warn($"group volume, {MemberForLog(_setups[i], i)}: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Shared volume + offset, clamped to 0..cap (the offset is added after the shared volume was capped, so
    /// it is capped again here); a muted (0) group stays muted everywhere.
    /// </summary>
    internal static double MemberVolume(double master, int offset, double cap = 100) =>
        master <= 0 || double.IsNaN(master) ? 0 : Math.Clamp(master + offset, 0, Math.Clamp(cap, 0, 100));

    public void LogStats()
    {
        if (_sender is not { } s) return;
        var legs = string.Join(" | ", s.Streams.Select((st, i) =>
            $"{MemberForLog(_setups[i], i)}: sent={st.PacketsSent} rtx={st.Retransmitted}/{st.RetransmitRequests} rtxMiss={st.RetransmitMisses}"));
        Log.Info($"group stats: fifo={_fifo.Depth * 1000.0 / RtpSender.SampleRate:F0}ms underruns={_fifo.Underruns} " +
                 $"overflows={_fifo.Overflows} sent={s.PacketsSent} late={s.LateWakeups} maxLate={s.MaxLateMs:F1}ms " +
                 $"skipped={s.SkippedPackets} | {legs}");
    }

    /// <summary>A member in the log: its number in the group (which stays in a diagnostic report) and its marked label.</summary>
    internal static string MemberForLog(MemberSetup setup, int index) => $"#{index + 1} {Log.Name(setup.Label)}";

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _outputStop?.Cancel();
        _localOutputs?.Dispose();
        _sender?.Dispose(); // stop the shared timeline first, then end every session
        _pump?.Dispose();
        _outputStop?.Dispose();
        IGroupMember?[] members;
        lock (_members) members = _members.ToArray();
        var teardowns = members.Where(m => m != null).Select(m => Task.Run(m!.Dispose)).ToArray();
        try { Task.WaitAll(teardowns); } catch (AggregateException ex) { Log.Warn($"group teardown: {ex.InnerException?.Message}"); }
    }
}
