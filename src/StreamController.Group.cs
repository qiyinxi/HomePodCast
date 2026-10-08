using System.Net;
using HomePodCast.Net;

namespace HomePodCast;

// Experimental stereo pair / multi-room streaming. Kept apart from the single-speaker loop in
// StreamController.cs, which runs exactly as before whenever no group is selected.
public sealed partial class StreamController
{
    private GroupRunner? _groupRunner;
    private int? _groupArrivalToRenderMs;
    private (string Tsid, List<ResolvedMember> Members)? _lastPair;

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
            Volume = volume;
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
        var options = new StreamOptions(EffectiveLatencyMs, null) { VolumeCapPercent = VolumeCapPercent, Effects = Effects };

        var setups = members.Select((m, i) => new MemberSetup(m.Label,
            async c => (IGroupMember)await AirPlayClient.PrepareAsync(m.Address, m.Port, options, plan.ChannelsFor(i), c),
            plan.VolumeOffsetFor(m.DeviceId))).ToList();
        var group = await SpeakerGroup.ConnectAsync(setups, _fifo, Muted ? 0 : Volume, ct, options.Effects);
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

        // Stereo pair: reuse last time's addresses while both still answer, else look the pair up again.
        if (_lastPair is { } last && last.Tsid == plan.PairTsid &&
            (await Task.WhenAll(last.Members.Select(m => IsReachable(m.Address, ct)))).All(ok => ok))
            return last.Members;
        var devices = await Mdns.BrowseAsync(TimeSpan.FromSeconds(3), ct);
        var found = StereoPairs.Members(devices, plan.PairTsid!);
        if (found.Count != 2)
            throw new AirPlayException(L.F("立体声对「{0}」需要两只音箱都在线（现在找到 {1} 只）", plan.Name, found.Count));
        var members = found.Select(d => new ResolvedMember(d.DeviceId, $"{d.Name} {d.Address}", d.Address, d.Port)).ToList();
        _lastPair = (plan.PairTsid!, members);
        return members;
    }

    /// <summary>Called by Stop() after the loop was asked to end.</summary>
    private void TearDownGroup() => Interlocked.Exchange(ref _groupRunner, null)?.TearDown();
}
