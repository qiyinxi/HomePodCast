using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast;

public sealed record GroupReceiverInteraction(AirPlayDevice Device, Exception Error, ConnectTrigger Trigger, long Generation);

public sealed partial class StreamController
{
    internal ReceiverGroupConnections.Connector? ConnectGroupReceiverOutput { get; set; }
    internal Func<string, CancellationToken, Task<AirPlayDevice>>? ResolveGroupReceiverDevice { get; set; }
    internal Func<string, AirPlayDevice?>? ManualGroupReceiverDevice { get; set; }
    internal Func<TimeSpan, CancellationToken, Task>? RetryGroupReceiverOutput { get; set; }
    internal IReadOnlyList<ReceiverGroupOutputStatus> ReceiverGroupOutputs => _groupRunner?.Current?.ReceiverOutputs ?? [];
    private ReceiverOutputSet? _preparingReceiverOutputs;
    public event Action<GroupReceiverInteraction>? GroupReceiverActionRequired;
    private bool GroupUsesReceiver(AirPlayDevice? device) => device != null && !GroupUsesRaop(device) && UsesThirdPartyPath(device);
    internal bool MatchesGroupReceiverInteraction(GroupReceiverInteraction request) =>
        Volatile.Read(ref _groupSelection) is { } selection && selection.Generation == request.Generation
        && selection.Members.Contains(GroupPlan.Key(request.Device.DeviceId));

    private static bool ExplicitlyOptionalApple(GroupPlan plan, AirPlayDevice? device, string id) =>
        plan.Kind == GroupKind.MultiRoom && (device == null || device.Kind is ReceiverKind.HomePod or ReceiverKind.AppleTv or ReceiverKind.Unknown)
        && plan.RequiredOutputs.Any(p => GroupPlan.Key(p.Key) == GroupPlan.Key(id) && !p.Value);

    private async Task<IGroupMember> PrepareRequiredAppleAsync(ResolvedMember member, StreamOptions options,
        ChannelMode channels, GroupRun run, CancellationToken ct)
    {
        try
        {
            return PrepareGroupMember != null ? await PrepareGroupMember(member, options, channels, ct)
                : await AirPlayClient.PrepareAsync(member.Address, member.Port, options, channels, ct);
        }
        catch (Exception error) when (ReceiverGroupFailures.RequiresUserAction(ReceiverGroupFailures.Normalize(error)))
        {
            var terminal = ReceiverGroupFailures.Normalize(error);
            var device = member.Device ?? new AirPlayDevice(member.Label, member.DeviceId, member.Address, member.Port,
                "AudioAccessory", new Dictionary<string, string>());
            var request = new GroupReceiverInteraction(device, terminal, run.Trigger, run.Generation);
            if (MatchesGroupReceiverInteraction(request)) _groupOutputFailures[GroupPlan.Key(member.DeviceId)] = terminal;
            if (!run.Streamed && run.Trigger is ConnectTrigger.Manual or ConnectTrigger.TakeOver && MatchesGroupReceiverInteraction(request))
                GroupReceiverActionRequired?.Invoke(request);
            throw terminal;
        }
        catch (Exception error)
        {
            if (Volatile.Read(ref _groupSelection)?.Generation == run.Generation && error is not OperationCanceledException)
                _groupOutputFailures[GroupPlan.Key(member.DeviceId)] = error;
            throw;
        }
    }

    internal bool ResumeGroupReceiverOutput(GroupReceiverInteraction request, ConnectTrigger trigger)
    {
        if (!MatchesGroupReceiverInteraction(request) || Volatile.Read(ref _activeGroupRun) is not { } run) return false;
        run.ReceiverUserTriggers[GroupPlan.Key(request.Device.DeviceId)] = trigger;
        return _groupRunner?.Current?.RetryReceiverOutput(request.Device.DeviceId) == true
            || Volatile.Read(ref _preparingReceiverOutputs)?.RetryOutput(request.Device.DeviceId) == true;
    }

    private ReceiverGroupOutputOptions ReceiverGroupOptions(GroupPlan plan, ResolvedMember member, int index, GroupRun run,
        int appleLatency, IReadOnlyDictionary<string, object?>? sessionExtras)
    {
        var initial = member.Device is { } known ? known with { Address = member.Address }
            : new AirPlayDevice(plan.Members.FirstOrDefault(m => GroupPlan.Key(m.DeviceId) == GroupPlan.Key(member.DeviceId))?.Name ?? member.Label,
                member.DeviceId, member.Address, member.Port, "", new Dictionary<string, string>());
        AirPlayDevice current = initial;
        int attempt = 0;
        var interactionTrigger = run.Trigger;
        bool explicitAttempt = false;
        bool apple = ExplicitlyOptionalApple(plan, member.Device, member.DeviceId);
        bool required = plan.RequiredOutputs.FirstOrDefault(p => GroupPlan.Key(p.Key) == GroupPlan.Key(member.DeviceId)) is { Key: not null } saved
            ? saved.Value : true;
        return new(member.DeviceId, initial.Name, async token =>
        {
            current = ResolveGroupReceiverDevice != null ? await ResolveGroupReceiverDevice(member.DeviceId, token)
                : ManualGroupReceiverDevice?.Invoke(member.DeviceId) is { } manual ? manual
                : ResolveGroupMembers != null && System.Net.IPAddress.IsLoopback(initial.Address) ? initial
                : (await Mdns.BrowseAsync(TimeSpan.FromSeconds(3), token))
                    .FirstOrDefault(d => GroupPlan.Key(d.DeviceId) == GroupPlan.Key(member.DeviceId))
                    ?? throw new AirPlayException(L.T("找不到音箱：请确认它已通电、和电脑在同一网络"));
            return new ReceiverGroupTarget(current, ReceiverSettings?.Invoke(member.DeviceId));
        }, new ReceiverConnectOptions(250, 0, CurrentGroupCap(), Pairings, ReceiverPassword?.Invoke(member.DeviceId), new EffectChain(), Log.Info)
            { TimingPorts = ReceiverTimingPorts })
        {
            Required = required, TrimMs = plan.DelayTrimFor(member.DeviceId), VolumeOffset = plan.VolumeOffsetFor(member.DeviceId),
            Channels = plan.ChannelsFor(index, member.DeviceId),
            InitialLatencyMs = apple ? appleLatency : 2000,
            Password = () => ReceiverPassword?.Invoke(member.DeviceId),
            PrepareApple = !apple ? null : async (target, token) =>
            {
                var options = new StreamOptions(appleLatency, 0)
                {
                    VolumeCapPercent = CurrentGroupCap(), Pairings = Pairings, DeviceId = member.DeviceId,
                    SessionSetupExtras = sessionExtras,
                };
                var resolved = new ResolvedMember(member.DeviceId, target.Device.Name, target.Device.Address, target.Device.Port) { Device = target.Device };
                var prepared = PrepareGroupMember != null ? await PrepareGroupMember(resolved, options, ChannelMode.Stereo, token)
                    : await AirPlayClient.PrepareAsync(resolved.Address, resolved.Port, options, ChannelMode.Stereo, token);
                return new AppleGroupMemberSession(prepared);
            },
            Connected = (protocol, processing) => ReceiverProtocolConnected?.Invoke(member.DeviceId, protocol, processing),
            BeforeConnect = async (target, token) =>
            {
                attempt = run.ReceiverAttempts.AddOrUpdate(GroupPlan.Key(member.DeviceId), 1, (_, count) => count + 1);
                explicitAttempt = run.ReceiverUserTriggers.TryRemove(GroupPlan.Key(member.DeviceId), out var requested);
                interactionTrigger = explicitAttempt ? requested : attempt == 1 && !run.Streamed ? run.Trigger : ConnectTrigger.Automatic;
                if (BusyReader is not { } reader) return;
                var trigger = interactionTrigger;
                bool ours = required && OwnSession.IsRecent(run.Key, UtcNow());
                if (TakeoverPolicy.SkipsCheck(trigger, ours)) return;
                var device = target.Device;
                var reading = await reader.ReadAsync([new(member.DeviceId, device.Name, device.Address, device.Port)], token);
                // Independent recovery never takes a busy receiver back, including a home-theater member.
                if (!required && trigger == ConnectTrigger.Automatic && reading.State == SpeakerBusy.Busy) throw new RaopBusyException();
                var action = TakeoverPolicy.Decide(trigger, reading.State, ours, reading.Holder, HomeTheaterMode, run.EndedBySpeaker);
                if (action == TakeoverAction.Ask) throw new RaopBusyException();
                if (action == TakeoverAction.Wait)
                {
                    if (!required) throw new RaopBusyException();
                    throw new IOException(L.F("{0}正在被其他设备使用，空闲后自动连接", device.Name));
                }
            },
            ActionRequired = error =>
            {
                if (!explicitAttempt && (attempt != 1 || run.Streamed) || interactionTrigger is not (ConnectTrigger.Manual or ConnectTrigger.TakeOver)) return;
                var request = new GroupReceiverInteraction(current, error, interactionTrigger, run.Generation);
                if (MatchesGroupReceiverInteraction(request)) GroupReceiverActionRequired?.Invoke(request);
            },
        };
    }
}
