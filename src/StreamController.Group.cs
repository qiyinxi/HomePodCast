using System.Net;
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

    internal sealed record ResolvedMember(string DeviceId, string Label, IPAddress Address, int Port);

    /// <summary>Finds every member of a group (tests replace it: no mDNS). Null: <see cref="ResolveGroupAsync"/>.</summary>
    internal Func<GroupPlan, CancellationToken, Task<List<ResolvedMember>>>? ResolveGroupMembers { get; set; }

    /// <summary>Opens one member's session (tests replace it: no RTSP). Null: AirPlayClient.PrepareAsync.</summary>
    internal Func<ResolvedMember, StreamOptions, ChannelMode, CancellationToken, Task<IGroupMember>>? PrepareGroupMember { get; set; }

    /// <summary>The sender of whatever is streaming (one speaker or a group), for stats and the sync probe.</summary>
    public RtpSender? ActiveSender => _client?.Sender ?? _groupRunner?.Current?.Sender;

    /// <param name="volumeAsOf">As for <see cref="Start"/>: a volume set after the request was made is kept.</param>
    public void StartGroup(GroupPlan plan, int latencyMs, double? volume, int? volumeAsOf = null)
    {
        lock (_lifecycle)
        {
            Stop();
            var cts = new CancellationTokenSource();
            var runner = new GroupRunner(plan.Name, ct => ConnectGroupAsync(plan, latencyMs, ct), Set);
            runner.FirewallBlocked += () => FirewallBlocked?.Invoke();
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

    private async Task<SpeakerGroup> ConnectGroupAsync(GroupPlan plan, int latencyMs, CancellationToken ct)
    {
        var members = await (ResolveGroupMembers ?? ResolveGroupAsync)(plan, ct);
        EnsureCapture(ct);

        EffectiveLatencyMs = Math.Max(SafeLatency(latencyMs), (_groupArrivalToRenderMs ?? 0) + SafetyMarginMs);
        if (EffectiveLatencyMs != latencyMs)
            Log.Warn($"latency {latencyMs} ms is below what the speakers can handle; using {EffectiveLatencyMs} ms");
        // One groupUUID per connection, shared by every member's session (see GroupPlan).
        var options = new StreamOptions(EffectiveLatencyMs, null)
        {
            VolumeCapPercent = VolumeCapPercent, Effects = Effects, SessionSetupExtras = plan.SessionExtras(Guid.NewGuid()),
        };

        var prepare = PrepareGroupMember ??
                      (async (m, o, channels, c) => await AirPlayClient.PrepareAsync(m.Address, m.Port, o, channels, c));
        var setups = members.Select((m, i) => new MemberSetup(m.Label,
            c => prepare(m, options, plan.ChannelsFor(i), c), plan.VolumeOffsetFor(m.DeviceId))).ToList();
        // A volume, mute or cap change from now on finds no group to send to until GroupRunner publishes it:
        // OnGroupConnected pushes again if anything changed meanwhile.
        _groupConnectCap = options.VolumeCapPercent;
        // The cap is read live, so lowering it while streaming also caps members with a positive offset.
        var group = await SpeakerGroup.ConnectAsync(setups, _fifo, Muted ? 0 : Volume, ct, options.Effects, () => VolumeCapPercent);
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
                IPAddress address;
                try { address = await ResolveAsync(m.DeviceId, m.Host, ct); }
                catch (AirPlayException ex) { throw new AirPlayException(L.F("{0}：{1}", m.Name, ex.Message)); }
                list.Add(new ResolvedMember(m.DeviceId, $"{m.Name} {address}", address, 7000));
            }
            return list;
        }

        // Stereo pair: reuse last time's addresses while they all still answer, else look the pair up again
        // (both members, or the leader alone with PairLeaderOnly).
        string key = $"{plan.PairTsid}|{plan.PairLeaderOnly}";
        if (_lastPair is { } last && last.Key == key &&
            (await Task.WhenAll(last.Members.Select(m => IsReachable(m.Address, ct)))).All(ok => ok))
            return last.Members;
        var devices = await Mdns.BrowseAsync(TimeSpan.FromSeconds(3), ct);
        var targets = plan.PairTargets(devices);
        var leader = StereoPairs.Leader(StereoPairs.Members(devices, plan.PairTsid!));
        Log.Info($"pair {plan.PairTsid}: {(plan.PairLeaderOnly ? "leader only" : "both members")}, " +
                 string.Join(", ", targets.Select(d => $"{d.Name} {d.Address}{(d == leader ? " (leader)" : "")}")));
        var members = targets.Select(d => new ResolvedMember(d.DeviceId, $"{d.Name} {d.Address}", d.Address, d.Port)).ToList();
        _lastPair = (key, members);
        return members;
    }

    /// <summary>Called by Stop() after the loop was asked to end.</summary>
    private void TearDownGroup() => Interlocked.Exchange(ref _groupRunner, null)?.TearDown();
}
