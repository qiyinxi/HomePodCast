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

    private sealed record ResolvedMember(string DeviceId, string Label, IPAddress Address, int Port);

    /// <summary>The sender of whatever is streaming (one speaker or a group), for stats and the sync probe.</summary>
    public RtpSender? ActiveSender => _client?.Sender ?? _groupRunner?.Current?.Sender;

    public void StartGroup(GroupPlan plan, int latencyMs, double? volume)
    {
        Stop();
        var cts = new CancellationTokenSource();
        var runner = new GroupRunner(plan.Name, ct => ConnectGroupAsync(plan, latencyMs, ct), Set);
        runner.FirewallBlocked += () => FirewallBlocked?.Invoke();
        lock (_lock)
        {
            _run = cts;
            _groupRunner = runner;
            Volume = volume is { } v ? VolumeLimit.Clamp(v, VolumeCapPercent) : null;
            _loop = Task.Run(async () =>
            {
                if (await runner.RunAsync(cts.Token)) // a speaker was taken over by another sender
                {
                    _capture?.Dispose();
                    _capture = null;
                }
            });
        }
    }

    private async Task<SpeakerGroup> ConnectGroupAsync(GroupPlan plan, int latencyMs, CancellationToken ct)
    {
        var members = await ResolveGroupAsync(plan, ct);
        EnsureCapture();

        EffectiveLatencyMs = Math.Max(SafeLatency(latencyMs), (_groupArrivalToRenderMs ?? 0) + SafetyMarginMs);
        if (EffectiveLatencyMs != latencyMs)
            Log.Warn($"latency {latencyMs} ms is below what the speakers can handle; using {EffectiveLatencyMs} ms");
        // One groupUUID per connection, shared by every member's session (see GroupPlan).
        var options = new StreamOptions(EffectiveLatencyMs, null)
        {
            VolumeCapPercent = VolumeCapPercent, Effects = Effects, SessionSetupExtras = plan.SessionExtras(Guid.NewGuid()),
        };

        var setups = members.Select((m, i) => new MemberSetup(m.Label,
            async c => (IGroupMember)await AirPlayClient.PrepareAsync(m.Address, m.Port, options, plan.ChannelsFor(i), c),
            plan.VolumeOffsetFor(m.DeviceId))).ToList();
        // The cap is read live, so lowering it while streaming also caps members with a positive offset.
        var group = await SpeakerGroup.ConnectAsync(setups, _fifo, Muted ? 0 : Volume, ct, options.Effects, () => VolumeCapPercent);
        if (group.ArrivalToRenderMs is { } a2r) _groupArrivalToRenderMs = a2r;
        Volume ??= group.MasterVolume;
        return group;
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
