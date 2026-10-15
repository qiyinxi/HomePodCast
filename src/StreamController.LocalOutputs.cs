using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast;

public sealed partial class StreamController
{
    /// <summary>Fake render-device factory for tests. Null opens only the explicitly configured endpoint.</summary>
    internal Func<string, ILocalRenderDevice>? OpenLocalOutput { get; set; }
    /// <summary>Configured capture identity while capture is still opening; null uses the active capture's identity.</summary>
    public Func<string?>? CapturedOutputId { get; set; }
    internal IReadOnlyList<LocalOutputStatus> LocalGroupOutputs => _groupRunner?.Current?.LocalOutputs ?? [];

    private string? LocalCapturedId() => CapturedOutputId?.Invoke() ?? Capture?.EndpointId;

    internal static int CoordinatedDelay(int airPlayMinimumMs, LocalOutputSet outputs, int? requestedMs = null)
    {
        int floor = Math.Max(100, Math.Max(airPlayMinimumMs, outputs.MinimumDelayMs)) + TimelinePump.HeadroomMs;
        return Math.Clamp(Math.Max(floor, requestedMs ?? floor), floor, 30000);
    }

    private async Task<SpeakerGroup> ConnectLocalGroupAsync(GroupPlan plan, int latencyMs, GroupRun run, CancellationToken ct)
    {
        if (run.Source != null) throw new ArgumentException("Discrete cinema sources do not use the stereo local-output group");
        // Refuse the whole selection before any receiver is contacted, including an optional colliding output.
        foreach (var output in plan.LocalOutputs) LocalOutputSink.CheckCapture(output.DeviceId, LocalCapturedId());
        var members = await (ResolveGroupMembers ?? ResolveGroupAsync)(plan, ct);
        await WaitUntilFreeAsync(TakeoverPolicy.RetryTrigger(run.Trigger, run.Streamed), run.Key,
            members.Select(m => new BusyTarget(m.DeviceId, m.Label, m.Address)).ToList(), ct, run.EndedBySpeaker, run.TakenFrom);
        EnsureCapture(ct);
        var locals = await LocalOutputSet.PrepareAsync(plan.LocalOutputs, LocalCapturedId, () => VolumeCapPercent, ct, OpenLocalOutput);
        try
        {
            int minimum = Math.Max(SafeLatency(latencyMs), (_groupArrivalToRenderMs ?? 0) + SafetyMarginMs);
            EffectiveLatencyMs = CoordinatedDelay(minimum, locals, plan.GroupDelayMs);
            // A long group delay belongs in the history, not in an arbitrarily large receiver jitter buffer.
            int protocolFloor = Math.Max((ArrivalToRenderMs ?? 85) + SafetyMarginMs, (_groupArrivalToRenderMs ?? 0) + SafetyMarginMs);
            int transportDelay = Math.Max(protocolFloor, Math.Min(2000, EffectiveLatencyMs - TimelinePump.HeadroomMs));
            var options = new StreamOptions(transportDelay, null)
            {
                VolumeCapPercent = VolumeCapPercent, SessionSetupExtras = plan.SessionExtras(Guid.NewGuid()),
            };
            var prepare = PrepareGroupMember ??
                          (async (m, o, channels, c) => await AirPlayClient.PrepareAsync(m.Address, m.Port, o, channels, c));
            var setups = members.Select((m, i) => new MemberSetup(m.Label,
                c => prepare(m, options, plan.ChannelsFor(i, m.DeviceId), c), plan.VolumeOffsetFor(m.DeviceId), m.DeviceId,
                plan.DelayTrimFor(m.DeviceId))).ToList();
            _groupConnectCap = options.VolumeCapPercent;
            var group = await SpeakerGroup.ConnectAsync(setups, _fifo, Muted ? 0 : Volume, ct, Effects,
                () => VolumeCapPercent, localOutputs: locals, groupDelayMs: EffectiveLatencyMs);
            if (group.ArrivalToRenderMs is { } a2r) _groupArrivalToRenderMs = a2r;
            if (!ct.IsCancellationRequested) Volume ??= group.MasterVolume;
            return group;
        }
        catch { locals.Dispose(); throw; }
    }
}
