using HomePodCast.Audio;

namespace HomePodCast.Net;

/// <summary>How to bring up one member of a group, and its offset from the shared volume (percentage points).</summary>
internal sealed record MemberSetup(string Label, Func<CancellationToken, Task<IGroupMember>> Prepare, int VolumeOffset = 0,
    string? Id = null, int DelayTrimMs = 0);

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
    private RaopOutputSet? _raopOutputs;
    private DlnaOutputSet? _dlnaOutputs;
    private ReceiverOutputSet? _receiverOutputs;
    private Exception? _dlnaFailure;
    private string? _auxiliaryFailureName;
    private TimelinePump? _pump;
    private CancellationTokenSource? _outputStop;
    private CancellationTokenSource? _connecting;

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
    public bool LostRequiresUserAction { get; private set; }

    public double MasterVolume { get; private set; }

    public OutputKind Kind => _members.Length > 0 || _receiverOutputs != null ? OutputKind.AirPlay2 : _raopOutputs != null ? OutputKind.Raop
        : _localOutputs != null ? OutputKind.Local : OutputKind.Dlna;
    public string Id => string.Join("|", _setups.Select(s => s.Id ?? s.Label)
        .Concat(_localOutputs?.Status.Select(s => s.Id) ?? []).Concat(_raopOutputs?.Status.Select(s => s.Id) ?? [])
        .Concat(_dlnaOutputs?.Status.Select(s => s.Id) ?? []).Concat(_receiverOutputs?.Status.Select(s => s.Id) ?? []));
    public string Name => string.Join(" + ", _setups.Select(s => s.Label)
        .Concat(_localOutputs?.Status.Select(s => s.Name) ?? []).Concat(_raopOutputs?.Status.Select(s => s.Name) ?? [])
        .Concat(_dlnaOutputs?.Status.Select(s => s.Name) ?? []).Concat(_receiverOutputs?.Status.Select(s => s.Name) ?? []));
    public int MinLatencyMs => Math.Max((int)Math.Ceiling((_sender?.LatencyFrames ?? 0) * 1000.0 / RtpSender.SampleRate),
        Math.Max(_receiverOutputs?.MinimumDelayMs ?? 0,
            Math.Max(_dlnaOutputs?.MinimumDelayMs ?? 0, Math.Max(_localOutputs?.MinimumDelayMs ?? 0, _raopOutputs?.MinimumDelayMs ?? 0))));
    public OutputState State => Volatile.Read(ref _disposed) != 0 ? OutputState.Stopped
        : _lost.Task.IsCompleted ? OutputState.Lost : OutputState.Streaming;
    public OutputStats Stats
    {
        get
        {
            var raop = _raopOutputs?.Status ?? [];
            return new((_sender?.PacketsSent ?? 0) + raop.Sum(s => s.Stats.PacketsSent) + (_receiverOutputs?.Status.Sum(s => s.PacketsSent) ?? 0),
                (_sender?.RetransmitRequests ?? 0) + raop.Sum(s => s.Stats.RetransmitRequests),
                (_sender?.Retransmitted ?? 0) + raop.Sum(s => s.Stats.Retransmitted),
                (_sender?.RetransmitMisses ?? 0) + raop.Sum(s => s.Stats.RetransmitMisses));
        }
    }
    public TimelineBuffer? Timeline { get; private set; }
    public IReadOnlyList<LocalOutputStatus> LocalOutputs => _localOutputs?.Status ?? [];
    public IReadOnlyList<RaopGroupOutputStatus> RaopOutputs => _raopOutputs?.Status ?? [];
    public IReadOnlyList<DlnaGroupOutputStatus> DlnaOutputs => _dlnaOutputs?.Status ?? [];
    public IReadOnlyList<ReceiverGroupOutputStatus> ReceiverOutputs => _receiverOutputs?.Status ?? [];
    internal bool RetryReceiverOutput(string id) => _receiverOutputs?.RetryOutput(id) == true;
    internal (string Name, Exception Error)? RequiredActionFailure => _receiverOutputs?.RequiredActionFailure
        ?? _dlnaOutputs?.RequiredActionFailure ?? _raopOutputs?.RequiredActionFailure;
    internal long TimelineProducedPackets => _pump?.ProducedPackets ?? 0;
    internal long TimelineSkippedPackets => _pump?.SkippedPackets ?? 0;
    internal bool TimelineRunning => _pump?.IsRunning == true;

    public void SetVolume(double percent, bool mute = false) => SetVolumePercent(mute ? 0 : percent);

    public int? ArrivalToRenderMs => _members.Length == 0 ? null : _members.Max(m => m?.ArrivalToRenderMs);

    /// <summary>
    /// Set up every member in parallel, RECORD on all of them, then start one shared timeline and FLUSH each
    /// session to it. If any member fails, whatever did connect is torn down and the failure is thrown.
    /// </summary>
    public static async Task<SpeakerGroup> ConnectAsync(IReadOnlyList<MemberSetup> setups, AudioFifo fifo,
        double? volumePercent, CancellationToken ct, IAudioEffect? effects = null, Func<double>? volumeCap = null,
        IGroupPacketSource? source = null, LocalOutputSet? localOutputs = null, int? groupDelayMs = null,
        RaopOutputSet? raopOutputs = null, DlnaOutputSet? dlnaOutputs = null,
        Func<(double? Percent, bool Muted)>? liveVolume = null, ReceiverOutputSet? receiverOutputs = null)
    {
        bool coordinated = groupDelayMs != null || localOutputs != null || raopOutputs != null || dlnaOutputs != null || receiverOutputs != null;
        if (setups.Count == 0 && !coordinated) throw new ArgumentException("empty group", nameof(setups));
        if (setups.Any(s => coordinated ? s.DelayTrimMs is < -5000 or > 5000 : s.DelayTrimMs is < 0 or > 500))
            throw new ArgumentOutOfRangeException(nameof(setups), "Speaker delay exceeds the timeline range");
        var group = new SpeakerGroup(setups, fifo, volumeCap);
        group._localOutputs = localOutputs;
        group._raopOutputs = raopOutputs;
        group._dlnaOutputs = dlnaOutputs;
        group._receiverOutputs = receiverOutputs;
        using var connecting = CancellationTokenSource.CreateLinkedTokenSource(ct);
        group._connecting = connecting;
        var attemptToken = connecting.Token;
        try
        {
            if (coordinated && (source != null || groupDelayMs is null))
                throw new ArgumentException("Auxiliary outputs require a coordinated capture timeline and group delay");
            if (setups.Count == 0 && localOutputs == null && group.AllAuxiliaryOutputsTerminal())
            {
                if (receiverOutputs?.FirstTerminalFailure is { } receiverFailure)
                    throw new GroupMemberException(receiverFailure.Name, receiverFailure.Error);
                if (dlnaOutputs != null)
                {
                    var first = dlnaOutputs.Status[0];
                    throw new GroupMemberException(first.Name, TerminalDlnaError(first));
                }
                throw new RaopRefusedException(string.Join("; ", raopOutputs!.Status.Select(s => $"{s.Name}: {s.Error}")));
            }
            if (coordinated)
            {
                group._outputStop = new CancellationTokenSource();
                if (localOutputs != null) _ = group.WatchLocalOutputs(localOutputs, group._outputStop.Token);
                if (raopOutputs != null) _ = group.WatchRaopOutputs(raopOutputs, group._outputStop.Token);
                if (dlnaOutputs != null) _ = group.WatchDlnaOutputs(dlnaOutputs, group._outputStop.Token);
                if (receiverOutputs != null) _ = group.WatchReceiverOutputs(receiverOutputs, group._outputStop.Token);
                if (setups.Count == 0 && localOutputs == null) _ = group.WatchAuxiliaryAvailability(group._outputStop.Token);
            }
            await group.PrepareAllAsync(attemptToken);
            var members = group._members.Select(m => m!).ToArray();

            // RECORD on every speaker first: nothing plays until all of them have accepted the stream.
            await group.EachAsync((m, _) => m.Record(), attemptToken);

            // One timeline: same RTP base and T0 for all; per-session keys, SSRCs and sequence numbers.
            long? coordinatedT0 = null;
            if (coordinated)
            {
                coordinatedT0 = MediaClock.Now + MediaClock.FromMs(receiverOutputs == null ? 250 : 500);
                int historyMs = groupDelayMs!.Value + setups.Select(s => Math.Max(0, s.DelayTrimMs)).DefaultIfEmpty().Max() + 2000;
                historyMs = Math.Max(historyMs, Math.Max(localOutputs?.HistoryMs(groupDelayMs.Value) ?? 0,
                    Math.Max(raopOutputs?.HistoryMs(groupDelayMs.Value) ?? 0, dlnaOutputs?.HistoryMs(groupDelayMs.Value) ?? 0)));
                historyMs = Math.Max(historyMs, receiverOutputs?.HistoryMs(groupDelayMs.Value) ?? 0);
                var pump = new TimelinePump(fifo, effects, historyMs, coordinatedT0);
                group._pump = pump;
                group.Timeline = pump.Timeline;
                pump.Failed += reason => group.OnOutputLost(group.Name, reason);
                if (members.Length > 0)
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
            long t0 = coordinatedT0 ?? MediaClock.Now + MediaClock.FromMs(250);
            group._pump?.Start(t0);
            if (members.Length > 0)
            {
                var senderFifo = coordinated ? new AudioFifo(RtpSender.SampleRate, 0, 0) : fifo;
                var sender = new RtpSender(members.Select(m => m.Target).ToArray(), members.Max(m => m.LatencyFrames), senderFifo)
                {
                    Effects = effects, // the common producer owns effects when coordinated
                    GroupSource = source,
                };
                group._sender = sender;
                sender.Start(t0 + (coordinated ? MediaClock.FromMs(TimelinePump.HeadroomMs) : 0));
                await group.EachAsync((m, i) => m.Flush(sender.Streams[i].FirstSeq, sender.RtpBase), attemptToken);
            }

            double CurrentMaster()
            {
                var live = liveVolume?.Invoke();
                return VolumeLimit.Clamp(live?.Muted == true ? 0 : live?.Percent ?? volumePercent
                    ?? (members.Length > 0 ? AirPlayClient.DbToPercent(members[0].InitialVolumeDb ?? -20.0) : 20), group.VolumeCapPercent);
            }
            group.MasterVolume = CurrentMaster();
            await group.EachAsync((m, i) =>
            {
                if (m is AirPlayClient client) client.VolumeCapPercent = group.VolumeCapPercent;
                m.SetVolumePercent(MemberVolume(CurrentMaster(), setups[i].VolumeOffset, group.VolumeCapPercent));
            }, attemptToken);
            group.MasterVolume = CurrentMaster();

            if (coordinated)
            {
                if (localOutputs != null) await localOutputs.StartAsync(group.Timeline!, groupDelayMs!.Value, group.MasterVolume, attemptToken);
                if (raopOutputs != null) await raopOutputs.StartAsync(group.Timeline!, groupDelayMs!.Value, group.MasterVolume, attemptToken);
                if (dlnaOutputs != null) await dlnaOutputs.StartAsync(group.Timeline!, groupDelayMs!.Value, group.MasterVolume,
                    group.MasterVolume <= 0, attemptToken);
                if (receiverOutputs != null) await receiverOutputs.StartAsync(group.Timeline!, groupDelayMs!.Value, group.MasterVolume,
                    group.MasterVolume <= 0, t0 + MediaClock.FromMs(TimelinePump.HeadroomMs), attemptToken);
                group.ThrowIfLost();
            }

            Log.Info($"group streaming: {string.Join(" + ", setups.Select((s, i) => MemberForLog(s, i)))}, " +
                     $"rtpBase={group._sender?.RtpBase} latency={group._sender?.LatencyFrames} frames; " +
                     $"local={localOutputs?.Status.Count ?? 0} raop={raopOutputs?.Status.Count ?? 0} dlna={dlnaOutputs?.Status.Count ?? 0} receivers={receiverOutputs?.Status.Count ?? 0}");
            return group;
        }
        catch
        {
            group.Dispose();
            if (!ct.IsCancellationRequested) group.ThrowIfLost();
            throw;
        }
        finally { Interlocked.Exchange(ref group._connecting, null); }
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
        CancelConnecting();
    }

    private void ThrowIfLost()
    {
        if (_receiverOutputs?.RequiredActionFailure is { } receiver) throw new GroupMemberException(receiver.Name, receiver.Error);
        if (_dlnaOutputs?.RequiredActionFailure is { } dlna) throw new GroupMemberException(dlna.Name, dlna.Error);
        if (_raopOutputs?.RequiredActionFailure is { } raop)
            throw new RaopRefusedException(L.F("{0}：{1}", raop.Name, raop.Error.Message))
                { LogMessage = $"{Log.Name(raop.Name)}: {raop.Error.Message}" };
        if (!_lost.Task.IsCompleted) return;
        if (_dlnaFailure != null) throw new GroupMemberException(_auxiliaryFailureName ?? Name, _dlnaFailure);
        if (LostRequiresUserAction || LostReason == RaopClient.ClosedByReceiver)
            throw new RaopRefusedException(_lost.Task.Result) { LogMessage = _lostForLog };
        throw new AirPlayException(_lost.Task.Result) { LogMessage = _lostForLog };
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

    private async Task WatchRaopOutputs(RaopOutputSet outputs, CancellationToken ct)
    {
        try
        {
            await outputs.Lost.WaitAsync(ct);
            OnOutputLost(outputs.LostOutputName ?? Name, outputs.LostReason ?? "RAOP output lost", outputs.LostRequiresUserAction);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private bool AllAuxiliaryOutputsTerminal() => (_raopOutputs != null || _dlnaOutputs != null || _receiverOutputs != null)
        && (_raopOutputs == null || _raopOutputs.Status.All(s => s.NeedsUserAction))
        && (_dlnaOutputs == null || _dlnaOutputs.AllTerminal)
        && (_receiverOutputs == null || _receiverOutputs.AllTerminal);

    private static Exception TerminalDlnaError(DlnaGroupOutputStatus status) => status.FailureKind switch
    {
        DlnaFailureKind.Policy => new DlnaPolicyException(status.Error ?? "DLNA network unavailable"),
        DlnaFailureKind.Unsupported => new DlnaUnsupportedException(status.Error ?? "DLNA format unavailable"),
        DlnaFailureKind.TakenOver => new DlnaTakenOverException(status.Error ?? "DLNA taken over"),
        DlnaFailureKind.Stopped => new DlnaStoppedException(status.Error ?? "DLNA stopped"),
        _ => new DlnaBusyException(status.Error ?? "DLNA requires user action"),
    };

    private async Task WatchAuxiliaryAvailability(CancellationToken ct)
    {
        try
        {
            while (!_lost.Task.IsCompleted)
            {
                if (AllAuxiliaryOutputsTerminal())
                {
                    if (_receiverOutputs?.FirstTerminalFailure is { } receiverFailure)
                    {
                        OnOutputLost(receiverFailure.Name, receiverFailure.Error.Message, true,
                            GroupRunner.Describe(receiverFailure.Error), receiverFailure.Error);
                        return;
                    }
                    var dlna = _dlnaOutputs?.Status.FirstOrDefault();
                    if (dlna != null) OnOutputLost(dlna.Name, dlna.Error ?? "DLNA output unavailable", true,
                        StreamController.DlnaMessage(dlna.FailureKind));
                    else
                    {
                        var raop = _raopOutputs!.Status[0];
                        OnOutputLost(raop.Name, raop.Error ?? "RAOP output unavailable", true);
                    }
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task WatchDlnaOutputs(DlnaOutputSet outputs, CancellationToken ct)
    {
        try
        {
            await outputs.Lost.WaitAsync(ct);
            OnOutputLost(outputs.LostOutputName ?? Name, outputs.LostReason ?? "DLNA output lost", outputs.LostRequiresUserAction,
                StreamController.DlnaMessage(DlnaFailures.Kind(outputs.LostException)), outputs.LostException);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task WatchReceiverOutputs(ReceiverOutputSet outputs, CancellationToken ct)
    {
        try
        {
            await outputs.Lost.WaitAsync(ct);
            OnOutputLost(outputs.LostOutputName ?? Name, outputs.LostReason ?? "Receiver output lost", outputs.LostRequiresUserAction,
                outputs.LostException == null ? null : GroupRunner.Describe(outputs.LostException), outputs.LostException);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private void OnOutputLost(string name, string reason, bool requiresUserAction = false, string? displayReason = null, Exception? failure = null)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (_lost)
        {
            if (_lost.Task.IsCompleted) return;
            _dlnaFailure = failure;
            _auxiliaryFailureName = name;
            LostReason = reason;
            LostRequiresUserAction = requiresUserAction;
            _lostForLog = $"{Log.Name(name)} lost ({reason})";
            Log.Warn($"group: {_lostForLog}; reconnecting required outputs");
            _lost.TrySetResult(L.F("{0}：{1}", name, displayReason ?? reason));
        }
        CancelConnecting();
    }

    private void CancelConnecting()
    {
        try { Volatile.Read(ref _connecting)?.Cancel(); }
        catch (ObjectDisposedException) { } // the completed setup has already released its token
        catch (AggregateException ex) { Log.Warn($"group setup cancellation: {ex.InnerException?.Message}"); }
    }

    /// <summary>Linked volume: each speaker gets the shared volume plus its own offset, never above the cap.</summary>
    public void SetVolumePercent(double master)
    {
        MasterVolume = master;
        _localOutputs?.SetVolume(master);
        _raopOutputs?.SetVolume(master);
        _dlnaOutputs?.SetVolume(master, master <= 0);
        _receiverOutputs?.SetVolume(master, master <= 0);
        double cap = VolumeCapPercent;
        for (int i = 0; i < _members.Length; i++)
        {
            if (_members[i] is not { } m) continue;
            try
            {
                if (m is AirPlayClient client) client.VolumeCapPercent = cap;
                m.SetVolumePercent(MemberVolume(master, _setups[i].VolumeOffset, cap));
            }
            catch (Exception ex) { Log.Warn($"group volume, {MemberForLog(_setups[i], i)}: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Shared volume + offset, clamped to 0..cap (the offset is added after the shared volume was capped, so
    /// it is capped again here); a muted (0) group stays muted everywhere.
    /// </summary>
    internal static double MemberVolume(double master, int offset, double cap = 100) =>
        master <= 0 || double.IsNaN(master) ? 0 : Math.Clamp(master + offset, 0, Math.Clamp(cap, 0, 100));

    internal async Task ConfirmMeasurementVolumeAsync(CancellationToken ct)
    {
        ThrowIfLost();
        double cap = VolumeCapPercent;
        if (cap > 25) throw new InvalidOperationException("No measurement volume ceiling");
        // Auxiliary sources already enforce the live cap digitally. Classic members need an acknowledged
        // volume command before the first test click; their ordinary slider publication is best effort.
        await Task.WhenAll(_members.Select((member, index) => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (member == null) return;
            double percent = MemberVolume(MasterVolume, _setups[index].VolumeOffset, cap);
            if (member is AirPlayClient client)
            {
                client.VolumeCapPercent = cap;
                client.SetGroupVolumePercentConfirmed(percent);
            }
            else member.SetVolumePercent(percent);
        }, ct))).ConfigureAwait(false);
        ThrowIfLost(); ct.ThrowIfCancellationRequested();
    }

    public void LogStats()
    {
        if (_receiverOutputs is { } receivers)
            Log.Info("group receiver stats: " + string.Join(" | ", receivers.Status.Select(s =>
                $"{Log.Name(s.Name)}: {s.State} {s.Protocol}/{(s.UsesPtp ? "PTP" : "NTP")} latency={s.LatencyMs}ms packets={s.PacketsSent}")));
        if (_dlnaOutputs is { } dlna)
            Log.Info("group DLNA stats: " + string.Join(" | ", dlna.Status.Select(s =>
                $"{Log.Name(s.Name)}: {s.State} {s.Format} estimatedLatency={s.EstimatedLatencyMs}ms "
                + $"drift={(s.HasClockEstimate ? s.DriftPpm.ToString("F0") : "unknown")}ppm confidence={s.ClockConfidence:F2} "
                + $"frames={s.PcmFramesSent} bytes={s.PayloadBytesSent} maxWrite={s.MaxWriteMs:F1}ms pendingWrite={s.PendingWriteMs:F1}ms")));
        var raop = _raopOutputs?.Status ?? [];
        if (raop.Count > 0)
            Log.Info("group RAOP stats: " + string.Join(" | ", raop.Select(output =>
                $"{Log.Name(output.Name)}: {output.State} sent={output.Stats.PacketsSent} rtx={output.Stats.Retransmitted}/{output.Stats.RetransmitRequests}")));
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
        CancelConnecting();
        _outputStop?.Cancel();
        // Stop capture consumption and packet production before any potentially slow remote teardown.
        _pump?.Dispose();
        _sender?.Dispose();
        _localOutputs?.Dispose();
        _raopOutputs?.Dispose();
        _dlnaOutputs?.Dispose();
        _receiverOutputs?.Dispose();
        _outputStop?.Dispose();
        IGroupMember?[] members;
        lock (_members) members = _members.ToArray();
        var teardowns = members.Where(m => m != null).Select(m => Task.Run(m!.Dispose)).ToArray();
        try { Task.WaitAll(teardowns); } catch (AggregateException ex) { Log.Warn($"group teardown: {ex.InnerException?.Message}"); }
    }
}
