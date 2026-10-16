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
    public event Action<GroupReceiverInteraction>? GroupReceiverActionRequired;
    private bool GroupUsesReceiver(AirPlayDevice? device) => device != null && !GroupUsesRaop(device) && UsesThirdPartyPath(device);
    internal bool MatchesGroupReceiverInteraction(GroupReceiverInteraction request) =>
        Volatile.Read(ref _groupSelection) is { } selection && selection.Generation == request.Generation
        && selection.Members.Contains(GroupPlan.Key(request.Device.DeviceId));

    private ReceiverGroupOutputOptions ReceiverGroupOptions(GroupPlan plan, ResolvedMember member, int index, GroupRun run)
    {
        var initial = member.Device! with { Address = member.Address };
        AirPlayDevice current = initial;
        int attempt = 0;
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
            Connected = (protocol, processing) => ReceiverProtocolConnected?.Invoke(member.DeviceId, protocol, processing),
            BeforeConnect = async (target, token) =>
            {
                attempt = run.ReceiverAttempts.AddOrUpdate(GroupPlan.Key(member.DeviceId), 1, (_, count) => count + 1);
                if (BusyReader is not { } reader) return;
                var trigger = attempt == 1 && !run.Streamed ? run.Trigger : ConnectTrigger.Automatic;
                bool ours = required && OwnSession.IsRecent(run.Key, UtcNow());
                if (TakeoverPolicy.SkipsCheck(trigger, ours)) return;
                var device = target.Device;
                var reading = await reader.ReadAsync([new(member.DeviceId, device.Name, device.Address, device.Port)], token);
                var action = TakeoverPolicy.Decide(trigger, reading.State, ours, reading.Holder, HomeTheaterMode, run.EndedBySpeaker);
                if (action == TakeoverAction.Ask) throw new RaopBusyException();
                if (action == TakeoverAction.Wait) throw new IOException(L.F("{0}正在被其他设备使用，空闲后自动连接", device.Name));
            },
            ActionRequired = error =>
            {
                if (attempt != 1 || run.Streamed || run.Trigger is not (ConnectTrigger.Manual or ConnectTrigger.TakeOver)) return;
                var request = new GroupReceiverInteraction(current, error, run.Trigger, run.Generation);
                if (MatchesGroupReceiverInteraction(request)) GroupReceiverActionRequired?.Invoke(request);
            },
        };
    }
}
