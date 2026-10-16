using System.Net;
using System.Collections.Frozen;
using HomePodCast.Net;

namespace HomePodCast;

// Experimental stereo pair / multi-room streaming. Kept apart from the single-speaker loop in
// StreamController.cs, which runs exactly as before whenever no group is selected.
public sealed partial class StreamController
{
    private GroupRunner? _groupRunner;
    private int? _groupArrivalToRenderMs;
    private (string Key, List<ResolvedMember> Members)? _lastPair;
    private double _groupConnectCap = 100;
    private Audio.IGroupPacketSource? _groupSource;
    private long _groupGeneration;
    private sealed record GroupSelection(long Generation, FrozenSet<string> Members);
    private GroupSelection? _groupSelection;

    internal sealed record ResolvedMember(string DeviceId, string Label, IPAddress Address, int Port)
    {
        public AirPlayDevice? Device { get; init; }
    }

    private AirPlayDevice? GroupDevice(string id) => ReceiverLookup?.Invoke(id) ?? ReceiverSettings?.Invoke(id)?.Device(id);

    private bool GroupUsesRaop(AirPlayDevice? device)
    {
        if (device == null || device.Kind is ReceiverKind.HomePod or ReceiverKind.AppleTv) return false;
        var forced = ReceiverSettings?.Invoke(device.DeviceId)?.ProtocolOverride ?? ReceiverProtocol.Auto;
        return forced == ReceiverProtocol.Raop || forced == ReceiverProtocol.Auto && UsesRaop(device);
    }

    internal bool MatchesRaopGroupInteraction(RaopInteraction request) => request.GroupGeneration is { } generation
        && Volatile.Read(ref _groupSelection) is { } selection && selection.Generation == generation
        && selection.Members.Contains(GroupPlan.Key(request.Device.DeviceId));

    /// <summary>Finds every member of a group (tests replace it: no mDNS). Null: <see cref="ResolveGroupAsync"/>.</summary>
    internal Func<GroupPlan, CancellationToken, Task<List<ResolvedMember>>>? ResolveGroupMembers { get; set; }

    /// <summary>Opens one member's session (tests replace it: no RTSP). Null: AirPlayClient.PrepareAsync.</summary>
    internal Func<ResolvedMember, StreamOptions, ChannelMode, CancellationToken, Task<IGroupMember>>? PrepareGroupMember { get; set; }

    /// <summary>The sender of whatever is streaming (one speaker or a group), for stats and the sync probe.</summary>
    public RtpSender? ActiveSender => _client?.Sender ?? _groupRunner?.Current?.Sender;

    /// <summary>
    /// The audio source of the group started last (the wireless cinema's file), until Stop or the next start: null while
    /// anything else plays. The cinema window compares it with its own source, so that a normal connection made while the
    /// cinema waited is never mistaken for the cinema, nor stopped by its Stop (review of #22).
    /// </summary>
    public Audio.IGroupPacketSource? GroupSource => Volatile.Read(ref _groupSource);

    /// <summary>
    /// One StartGroup's connection: why it connects, the selection's key, whether it has streamed yet, and whether a
    /// speaker ended its last session itself (another sender took it: a home theater's Apple TV is then not fought).
    /// </summary>
    private sealed class GroupRun(ConnectTrigger trigger, string key, string? takenFrom, Audio.IGroupPacketSource? source, long generation)
    {
        public ConnectTrigger Trigger { get; } = trigger;
        public string Key { get; } = key;
        public string? TakenFrom { get; } = takenFrom;
        public Audio.IGroupPacketSource? Source { get; } = source;
        public long Generation { get; } = generation;
        public System.Collections.Concurrent.ConcurrentDictionary<string, int> RaopAttempts { get; } = new();
        public System.Collections.Concurrent.ConcurrentDictionary<string, int> ReceiverAttempts { get; } = new();
        public volatile bool Streamed;
        public volatile bool EndedBySpeaker;
    }

    /// <param name="volumeAsOf">As for <see cref="Start"/>: a volume set after the request was made is kept.</param>
    /// <param name="trigger">As for <see cref="Start"/>; every member is checked.</param>
    /// <param name="takenFrom">As for <see cref="Start"/>.</param>
    public void StartGroup(GroupPlan plan, int latencyMs, double? volume, int? volumeAsOf = null, ConnectTrigger trigger = ConnectTrigger.Manual,
        string? takenFrom = null, Audio.IGroupPacketSource? source = null)
    {
        lock (_lifecycle)
        {
            Stop();
            if (source != null && (plan.LocalOutputs.Count > 0 || plan.DlnaOutputs.Count > 0 || plan.Members.Any(m => GroupUsesRaop(GroupDevice(m.DeviceId)))))
            {
                source.Dispose();
                Set(StreamState.Idle, L.T("本地影音文件暂不支持 AirPlay 1、本机或 DLNA 输出分组。"));
                return;
            }
            if (source != null && plan.Members.Any(m => GroupUsesReceiver(GroupDevice(m.DeviceId))))
            {
                source?.Dispose();
                Set(StreamState.Idle, L.T("本地文件分组暂不支持第三方 AirPlay 设备；请使用 HomePod。"));
                return;
            }
            var cts = new CancellationTokenSource();
            Volatile.Write(ref _groupSource, source); // Stop disposes it (TearDownGroup)
            long generation = Interlocked.Increment(ref _groupGeneration);
            Volatile.Write(ref _groupSelection, new(generation, plan.Members.Select(m => GroupPlan.Key(m.DeviceId)).ToFrozenSet()));
            var run = new GroupRun(trigger, OwnSession.KeyOf(plan), takenFrom, source, generation);
            // The connected line says when the group was taken over from a home theater's Apple TV.
            var runner = new GroupRunner(plan.Name, ct => ConnectGroupAsync(plan, latencyMs, run, ct),
                (state, text) => Set(state, state == StreamState.Streaming && _takenFrom != null ? ConnectedText(plan.Name) : text),
                takeoverGrace: TakeoverGrace);
            runner.FirewallBlocked += () => FirewallBlocked?.Invoke();
            runner.Connected += _ =>
            {
                run.Streamed = true;
                OwnSession.Began(run.Key, UtcNow());
            };
            runner.StreamEnded += (lostReason, stopped) =>
            {
                if (lostReason is EventChannel.ClosedBySpeaker or Net.RaopClient.ClosedByReceiver) OwnSession.Forget(); // another sender took a member over
                else OwnSession.Ended(UtcNow(), clean: stopped);
                run.EndedBySpeaker = lostReason is EventChannel.ClosedBySpeaker or Net.RaopClient.ClosedByReceiver;
            };
            runner.StoppedForUserAction += OwnSession.Forget;
            runner.Connected += OnGroupConnected;
            lock (_lock)
            {
                _run = cts;
                _groupRunner = runner;
                TakeStartVolume(volume, volumeAsOf);
                _loop = Task.Run(async () =>
                {
                    if (await runner.RunAsync(cts.Token)) DisposeCapture(); // a speaker was taken over by another sender
                });
            }
        }
    }

    private async Task<SpeakerGroup> ConnectGroupAsync(GroupPlan plan, int latencyMs, GroupRun run, CancellationToken ct)
    {
        // A capture collision is refused before resolution, including when that output is optional.
        foreach (var output in plan.LocalOutputs) LocalOutputSink.CheckCapture(output.DeviceId, LocalCapturedId());
        var members = (await (ResolveGroupMembers ?? ResolveGroupAsync)(plan, ct)).Select(m =>
        {
            var device = m.Device ?? GroupDevice(m.DeviceId);
            return m with { Device = device, Port = GroupUsesRaop(device) ? device!.RaopPort ?? device.Port : m.Port };
        }).ToList();
        ct.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _groupSelection)?.Generation == run.Generation)
            Volatile.Write(ref _groupSelection, new(run.Generation, members.Select(m => GroupPlan.Key(m.DeviceId)).ToFrozenSet()));
        if (run.Source != null && members.Any(m => GroupUsesReceiver(m.Device)))
        {
            if (run.Source != null && Interlocked.CompareExchange(ref _groupSource, null, run.Source) == run.Source)
                run.Source.Dispose();
            throw new RaopRefusedException(L.T("本地文件分组暂不支持第三方 AirPlay 设备；请使用 HomePod。"));
        }
        if (plan.LocalOutputs.Count > 0 || plan.DlnaOutputs.Count > 0 || members.Any(m => GroupUsesRaop(m.Device) || GroupUsesReceiver(m.Device)))
        {
            if (run.Source != null)
            {
                if (Interlocked.CompareExchange(ref _groupSource, null, run.Source) == run.Source) run.Source.Dispose();
                throw new RaopRefusedException(L.T("本地影音文件暂不支持 AirPlay 1、本机或 DLNA 输出分组。"));
            }
            return await ConnectLocalGroupAsync(plan, members, latencyMs, run, ct);
        }
        await WaitUntilFreeAsync(TakeoverPolicy.RetryTrigger(run.Trigger, run.Streamed), run.Key,
            members.Select(m => new BusyTarget(m.DeviceId, m.Label, m.Address, m.Port)).ToList(), ct, run.EndedBySpeaker, run.TakenFrom);
        if (run.Source == null) EnsureCapture(ct); // the cinema plays its file: no system capture, no effects chain

        EffectiveLatencyMs = Math.Max(SafeLatency(latencyMs), (_groupArrivalToRenderMs ?? 0) + SafetyMarginMs);
        if (EffectiveLatencyMs != latencyMs)
            Log.Warn($"latency {latencyMs} ms is below what the speakers can handle; using {EffectiveLatencyMs} ms");
        // One groupUUID per connection, shared by every member's session (see GroupPlan).
        var options = new StreamOptions(EffectiveLatencyMs, null)
        {
            VolumeCapPercent = VolumeCapPercent, Effects = run.Source == null ? Effects : null, SessionSetupExtras = plan.SessionExtras(Guid.NewGuid()),
        };

        var prepare = PrepareGroupMember ??
                      (async (m, o, channels, c) => await AirPlayClient.PrepareAsync(m.Address, m.Port, o, channels, c));
        var setups = members.Select((m, i) => new MemberSetup(m.Label,
            c => prepare(m, options, plan.ChannelsFor(i, m.DeviceId), c), plan.VolumeOffsetFor(m.DeviceId), m.DeviceId,
            plan.DelayTrimFor(m.DeviceId))).ToList();
        // A volume, mute or cap change from now on finds no group to send to until GroupRunner publishes it:
        // OnGroupConnected pushes again if anything changed meanwhile.
        _groupConnectCap = options.VolumeCapPercent;
        // The cap is read live, so lowering it while streaming also caps members with a positive offset.
        var group = await SpeakerGroup.ConnectAsync(setups, _fifo, Muted ? 0 : Volume, ct, options.Effects, () => VolumeCapPercent, run.Source);
        if (group.ArrivalToRenderMs is { } a2r) _groupArrivalToRenderMs = a2r;
        if (!ct.IsCancellationRequested) Volume ??= group.MasterVolume;
        return group;
    }

    /// <summary>
    /// GroupRunner published the group (PushVolume reaches it from now on). Members get the current cap, and the
    /// volume goes out again if it, the mute or the cap changed while they were connecting, as the single-speaker
    /// loop does after connecting.
    /// </summary>
    private void OnGroupConnected(SpeakerGroup group)
    {
        double cap = VolumeCapPercent;
        foreach (var m in group.Members)
            if (m is AirPlayClient member) member.VolumeCapPercent = cap;
        double? want = Muted ? 0 : Volume;
        if (cap != _groupConnectCap || want is { } w && VolumeLimit.Clamp(w, cap) != group.MasterVolume)
            PushVolume();
    }

    /// <summary>Addresses of every member; throws unless all of them are online.</summary>
    private async Task<List<ResolvedMember>> ResolveGroupAsync(GroupPlan plan, CancellationToken ct)
    {
        if (plan.Kind == GroupKind.MultiRoom)
        {
            var list = new List<ResolvedMember>();
            foreach (var m in plan.Members)
            {
                var device = GroupDevice(m.DeviceId);
                IPAddress address;
                try
                {
                    if (GroupUsesRaop(device))
                    {
                        // RAOP's advertised service may live on any port. The original resolver probes fixed 7000.
                        if (device!.Address.Equals(IPAddress.None))
                            device = (await Mdns.BrowseAsync(TimeSpan.FromSeconds(3), ct))
                                .FirstOrDefault(d => GroupPlan.Key(d.DeviceId) == GroupPlan.Key(m.DeviceId))
                                ?? throw new AirPlayException(L.T("找不到音箱：请确认它已通电、和电脑在同一网络"));
                        address = device.Address;
                    }
                    else if (GroupUsesReceiver(device)) address = device!.Address;
                    else address = await ResolveAsync(m.DeviceId, m.Host, ct);
                }
                catch (AirPlayException ex)
                {
                    throw new AirPlayException(L.F("{0}：{1}", m.Name, ex.Message)) { LogMessage = $"{Log.Name(m.Name)}: {AirPlayException.ForLog(ex)}" };
                }
                list.Add(new ResolvedMember(m.DeviceId, $"{m.Name} {address}", address,
                    GroupUsesRaop(device) ? device!.RaopPort ?? device.Port : device?.Port ?? 7000)
                    { Device = device is null ? null : device with { Address = address } });
            }
            return list;
        }

        // Stereo pair: reuse last time's addresses while they all still answer, else look the pair up again
        // (both members, or the leader alone with PairLeaderOnly).
        string key = $"{plan.PairTsid}|{plan.PairLeaderOnly}";
        if (_lastPair is { } last && last.Key == key &&
            (await Task.WhenAll(last.Members.Select(m => IsReachable(m.Address, 7000, ct)))).All(ok => ok))
            return last.Members;
        var devices = await Mdns.BrowseAsync(TimeSpan.FromSeconds(3), ct);
        var targets = plan.PairTargets(devices);
        var leader = StereoPairs.Leader(StereoPairs.Members(devices, plan.PairTsid!));
        Log.Info($"pair {plan.PairTsid}: {(plan.PairLeaderOnly ? "leader only" : "both members")}, " +
                 string.Join(", ", targets.Select(d => $"{Log.Name(d.Name)} {d.Address}{(d == leader ? " (leader)" : "")}")));
        var members = targets.Select(d => new ResolvedMember(d.DeviceId, $"{d.Name} {d.Address}", d.Address, d.Port) { Device = d }).ToList();
        _lastPair = (key, members);
        return members;
    }

    /// <summary>Called by Stop() after the loop was asked to end.</summary>
    private void TearDownGroup()
    {
        Interlocked.Increment(ref _groupGeneration);
        Volatile.Write(ref _groupSelection, null);
        Interlocked.Exchange(ref _groupRunner, null)?.TearDown();
        Interlocked.Exchange(ref _groupSource, null)?.Dispose();
    }
}
