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
    internal IReadOnlyList<RaopGroupOutputStatus> RaopGroupOutputs => _groupRunner?.Current?.RaopOutputs ?? [];
    internal Func<RaopGroupOutputOptions, RaopOptions, CancellationToken, Task<RaopClient>>? ConnectGroupRaopOutput { get; set; }
    internal Func<TimeSpan, CancellationToken, Task>? RetryGroupRaopOutput { get; set; }

    private string? LocalCapturedId() => CapturedOutputId?.Invoke() ?? Capture?.EndpointId;

    internal static int CoordinatedDelay(int airPlayMinimumMs, LocalOutputSet outputs, int? requestedMs = null)
        => CoordinatedDelay(airPlayMinimumMs, outputs, null, requestedMs);

    internal static int CoordinatedDelay(int airPlayMinimumMs, LocalOutputSet? locals, RaopOutputSet? raops, int? requestedMs = null)
        => CoordinatedDelay(airPlayMinimumMs, locals, raops, null, requestedMs);

    internal static int CoordinatedDelay(int airPlayMinimumMs, LocalOutputSet? locals, RaopOutputSet? raops, DlnaOutputSet? dlnas, int? requestedMs = null)
        => CoordinatedDelay(airPlayMinimumMs, locals, raops, dlnas, null, requestedMs);

    internal static int CoordinatedDelay(int airPlayMinimumMs, LocalOutputSet? locals, RaopOutputSet? raops, DlnaOutputSet? dlnas,
        ReceiverOutputSet? receivers, int? requestedMs = null)
    {
        int floor = Math.Max(100, Math.Max(airPlayMinimumMs,
            Math.Max(receivers?.MinimumDelayMs ?? 0, Math.Max(dlnas?.MinimumDelayMs ?? 0,
                Math.Max(locals?.MinimumDelayMs ?? 0, raops?.MinimumDelayMs ?? 0))))) + TimelinePump.HeadroomMs;
        return Math.Clamp(Math.Max(floor, requestedMs ?? floor), floor, 30000);
    }

    private async Task<SpeakerGroup> ConnectLocalGroupAsync(GroupPlan plan, IReadOnlyList<ResolvedMember> members,
        int latencyMs, GroupRun run, CancellationToken ct)
    {
        // Preserve the original member index before filtering: a legacy [RAOP left, AP2 right] split stays that way.
        var indexed = members.Select((member, index) => (Member: member, Index: index)).ToArray();
        var airplay = indexed.Where(m => !GroupUsesRaop(m.Member.Device) && !GroupUsesReceiver(m.Member.Device)).ToArray();
        var raop = indexed.Where(m => GroupUsesRaop(m.Member.Device)).ToArray();
        var receivers = indexed.Where(m => GroupUsesReceiver(m.Member.Device)).ToArray();
        var required = airplay.Select(m => m.Member).Concat(raop.Where(m => plan.IsRequiredOutput(m.Member.DeviceId)).Select(m => m.Member)).ToArray();
        await WaitUntilFreeAsync(TakeoverPolicy.RetryTrigger(run.Trigger, run.Streamed), run.Key,
            required.Select(m => new BusyTarget(m.DeviceId, m.Label, m.Address, m.Port)).ToList(), ct, run.EndedBySpeaker, run.TakenFrom);
        EnsureCapture(ct);
        LocalOutputSet? locals = null;
        RaopOutputSet? raops = null;
        DlnaOutputSet? dlnas = null;
        ReceiverOutputSet? receiverOutputs = null;
        try
        {
            if (plan.LocalOutputs.Count > 0)
                locals = await LocalOutputSet.PrepareAsync(plan.LocalOutputs, LocalCapturedId, CurrentGroupCap, ct, OpenLocalOutput);
            if (raop.Length > 0)
            {
                var outputs = raop.Select(m => RaopGroupOptions(plan, m.Member, m.Index, run)).ToArray();
                raops = await RaopOutputSet.PrepareAsync(outputs, CurrentGroupCap, ct, ConnectGroupRaopOutput, RetryGroupRaopOutput);
            }
            if (plan.DlnaOutputs.Count > 0)
                dlnas = await DlnaOutputSet.PrepareAsync(plan.DlnaOutputs.Select(DlnaGroupOptions).ToArray(),
                    CurrentGroupCap, ct, ConnectGroupDlnaOutput, RetryGroupDlnaOutput, liveVolume: CurrentGroupVolume);
            if (receivers.Length > 0)
                receiverOutputs = await ReceiverOutputSet.PrepareAsync(receivers.Select(m => ReceiverGroupOptions(plan, m.Member, m.Index, run)).ToArray(),
                    CurrentGroupCap, ct, ConnectGroupReceiverOutput, RetryGroupReceiverOutput, liveVolume: CurrentGroupVolume);
            int minimum = airplay.Length == 0 ? 0 : Math.Max(SafeLatency(latencyMs), (_groupArrivalToRenderMs ?? 0) + SafetyMarginMs);
            EffectiveLatencyMs = CoordinatedDelay(minimum, locals, raops, dlnas, receiverOutputs, plan.GroupDelayMs);
            // A long group delay belongs in the history, not in an arbitrarily large receiver jitter buffer.
            int protocolFloor = Math.Max((ArrivalToRenderMs ?? 85) + SafetyMarginMs, (_groupArrivalToRenderMs ?? 0) + SafetyMarginMs);
            int transportDelay = Math.Max(protocolFloor, Math.Min(2000, EffectiveLatencyMs - TimelinePump.HeadroomMs));
            var options = new StreamOptions(transportDelay, null)
            {
                VolumeCapPercent = VolumeCapPercent, SessionSetupExtras = plan.SessionExtras(Guid.NewGuid()),
            };
            var prepare = PrepareGroupMember ??
                          (async (m, o, channels, c) => await AirPlayClient.PrepareAsync(m.Address, m.Port, o, channels, c));
            var setups = airplay.Select(m => new MemberSetup(m.Member.Label,
                c => prepare(m.Member, options, plan.ChannelsFor(m.Index, m.Member.DeviceId), c), plan.VolumeOffsetFor(m.Member.DeviceId), m.Member.DeviceId,
                plan.DelayTrimFor(m.Member.DeviceId))).ToList();
            _groupConnectCap = options.VolumeCapPercent;
            SpeakerGroup group;
            try
            {
                group = await SpeakerGroup.ConnectAsync(setups, _fifo, Muted ? 0 : Volume, ct, Effects,
                    CurrentGroupCap, localOutputs: locals, groupDelayMs: EffectiveLatencyMs, raopOutputs: raops, dlnaOutputs: dlnas,
                    liveVolume: plan.DlnaOutputs.Count > 0 || receiverOutputs != null ? CurrentGroupVolume : null,
                    receiverOutputs: receiverOutputs);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && receiverOutputs?.Lost.IsCompleted == true)
            {
                throw new GroupMemberException(receiverOutputs.LostOutputName ?? plan.Name,
                    receiverOutputs.LostException ?? new IOException(receiverOutputs.LostReason));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && dlnas?.Lost.IsCompleted == true)
            {
                string reason = dlnas.LostReason ?? await dlnas.Lost;
                Exception error = dlnas.LostException ?? new IOException(reason);
                throw new GroupMemberException(dlnas.LostOutputName ?? plan.Name, error);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && raops?.Lost.IsCompleted == true)
            {
                // Required RAOP can close while AP2 is still preparing. Keep its reason instead of the set's
                // internal cancellation, so a refusal cannot become an endless whole-group reconnect.
                string reason = raops.LostReason ?? await raops.Lost;
                Exception error = raops.LostRequiresUserAction || reason == Net.RaopClient.ClosedByReceiver
                    ? new RaopRefusedException(reason) : new IOException(reason);
                throw new GroupMemberException(raops.LostOutputName ?? plan.Name, error);
            }
            catch (Exception) when (!ct.IsCancellationRequested && raops?.Status.Any(s => s.Required && s.NeedsUserAction) == true)
            {
                // Volume publication can discover a required receiver's orderly close before its Lost watcher
                // runs. The slot already records this refusal; keep it typed without changing AP2's grace rule.
                var terminal = raops.Status.First(s => s.Required && s.NeedsUserAction);
                throw new GroupMemberException(terminal.Name, new RaopRefusedException(terminal.Error ?? "RAOP output unavailable"));
            }
            if (group.ArrivalToRenderMs is { } a2r) _groupArrivalToRenderMs = a2r;
            if (!ct.IsCancellationRequested) Volume ??= group.MasterVolume;
            return group;
        }
        catch { locals?.Dispose(); raops?.Dispose(); dlnas?.Dispose(); receiverOutputs?.Dispose(); throw; }
    }

    private RaopGroupOutputOptions RaopGroupOptions(GroupPlan plan, ResolvedMember member, int index, GroupRun run)
    {
        var device = member.Device! with { Address = member.Address };
        int latency = CompatibilityLatency?.Invoke(member.DeviceId)
            ?? ReceiverSettings?.Invoke(member.DeviceId)?.CompatibilityLatencyMs ?? 2000;
        if (latency is not (1000 or 1500 or 2000)) latency = 2000;
        bool required = plan.IsRequiredOutput(member.DeviceId);
        int attempt = 0;
        return new(member.DeviceId, device.Name, member.Address, device.RaopPort ?? device.Port, new RaopOptions
        {
            LatencyMs = latency, Txt = device.RaopTxt ?? device.Txt,
            Password = ReceiverPassword?.Invoke(member.DeviceId), VolumeCapPercent = VolumeCapPercent, Log = Log.Info,
        })
        {
            Required = required, Channels = plan.ChannelsFor(index, member.DeviceId),
            VolumeOffset = plan.VolumeOffsetFor(member.DeviceId), TrimMs = plan.DelayTrimFor(member.DeviceId),
            BeforeConnect = async token =>
            {
                attempt = run.RaopAttempts.AddOrUpdate(GroupPlan.Key(member.DeviceId), 1, (_, count) => count + 1);
                if (BusyReader is not { } reader) return;
                var trigger = required ? TakeoverPolicy.RetryTrigger(run.Trigger, run.Streamed)
                    : attempt == 1 && !run.Streamed ? run.Trigger : ConnectTrigger.Automatic;
                bool ours = required && OwnSession.IsRecent(run.Key, UtcNow());
                if (TakeoverPolicy.SkipsCheck(trigger, ours)) return;
                var reading = await reader.ReadAsync([new(member.DeviceId, device.Name, member.Address, device.RaopPort ?? device.Port)], token);
                var action = TakeoverPolicy.Decide(trigger, reading.State, ours, reading.Holder, HomeTheaterMode, run.EndedBySpeaker);
                if (action == TakeoverAction.Ask) throw new RaopBusyException();
                if (action == TakeoverAction.Wait)
                    throw new IOException(L.F("{0}正在被其他设备使用，空闲后自动连接", device.Name));
            },
            ActionRequired = error =>
            {
                if (attempt != 1 || run.Streamed || run.Trigger is not (ConnectTrigger.Manual or ConnectTrigger.TakeOver)) return;
                var request = new RaopInteraction(device, run.Trigger) { GroupGeneration = run.Generation };
                if (!MatchesRaopGroupInteraction(request)) return;
                if (error is RaopPasswordRequiredException) RaopPasswordRequired?.Invoke(request);
                else if (error is RaopBusyException && run.Trigger == ConnectTrigger.Manual) RaopTakeoverRequired?.Invoke(request);
            },
        };
    }
}
