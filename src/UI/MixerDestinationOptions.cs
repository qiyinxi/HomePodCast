using HomePodCast.Net;

namespace HomePodCast.UI;

internal sealed record MixerDestinationOption(MixerRoute? Route, string Label, bool Enabled = true, string? Reason = null);

/// <summary>Shared, pure option source for default, per-app and replace-all pickers; no device discovery or activation.</summary>
internal static class MixerDestinationOptions
{
    internal static List<MixerDestinationOption> Build(AppConfig config, IReadOnlyList<AirPlayDevice> scan,
        MixerRoute? selected, bool followDefault = false, Func<MixerRoute, MixerEditOutcome>? validate = null)
    {
        var result = new List<MixerDestinationOption>();
        var routing = MixerRoutes.Read(config).Settings;
        if (followDefault) result.Add(new(null, L.F("跟随默认：{0}", Name(config, routing?.Default ?? MixerRoute.Current, scan))));
        Add(MixerRoute.Current, Name(config, MixerRoute.Current, scan));
        Add(MixerRoute.Local, L.T("本机"));
        var speakers = new List<(string Id, string Name, string Protocol, bool Online)>();
        if (config.DeviceId != null) speakers.Add(("airplay:" + config.DeviceId, config.DeviceName ?? config.DeviceId, "AirPlay", Found(config.DeviceId)));
        speakers.AddRange((config.GroupSpeakers ?? []).Select(d => ("airplay:" + d.DeviceId, d.Name, "AirPlay", Found(d.DeviceId))));
        speakers.AddRange(config.ManualDevices.Select(d => ("airplay:" + d.DeviceId, d.Name, "AirPlay", Found(d.DeviceId))));
        speakers.AddRange(config.ReceiverSettings.Select(p => ("airplay:" + p.Key, p.Value.Name,
            p.Value.ProtocolOverride == ReceiverProtocol.Raop ? "AirPlay 1" : "AirPlay", Found(p.Key))));
        speakers.AddRange(config.GroupDlnaOutputs.Select(d => ("dlna:" + d.Udn, d.Name, "DLNA", false)));
        speakers.AddRange(config.GroupLocalOutputs.Select(d => ("local:" + d.DeviceId, d.Name, d.IsBluetooth ? L.T("蓝牙") : L.T("本机输出"), false)));
        foreach (var output in speakers.DistinctBy(d => MixerRoutes.SpeakerKey(d.Id)).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var route = new MixerRoute(new MixerOutput(MixerOutputKind.Speaker, output.Id));
            string label = output.Name + " · " + output.Protocol;
            if (!output.Online) label = L.F("{0}（未发现）", label);
            Add(route, label);
            if (!output.Id.StartsWith("local:")) Add(route with { AlsoLocal = true }, L.F("{0} + {1}", label, L.T("本机")));
        }
        foreach (var group in config.SavedOutputGroups.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var route = new MixerRoute(new MixerOutput(MixerOutputKind.Group, group.Id));
            Add(route, group.Name + " · " + L.T("音箱组"));
            Add(route with { AlsoLocal = true }, L.F("{0} + {1}", group.Name, L.T("本机")));
        }
        if (selected != null && !result.Any(o => Same(o.Route, selected)))
            result.Add(new(selected, L.T("去处已删除，请重新选择"), false, L.T("去处已删除，请重新选择")));
        return result;

        bool Found(string id) => StereoPairs.TryGetTsid(id, out var tsid) ? StereoPairs.Members(scan, tsid).Count == 2
            : scan.Any(d => GroupPlan.Key(d.DeviceId) == GroupPlan.Key(id));
        void Add(MixerRoute route, string label)
        {
            var resolved = MixerDestinationResolver.Resolve(config, route, scan);
            var outcome = validate?.Invoke(route);
            string? reason = !resolved.Available && resolved.Error != MixerRoutingError.NoSelection ? Error(resolved.Error) :
                outcome is { Result: not MixerEditResult.Applied } ? Error(outcome, config) : null;
            result.Add(new(route, reason == null ? label : label + " · " + reason, reason == null, reason));
        }
    }

    internal static bool Same(MixerRoute? a, MixerRoute? b) => a == null || b == null ? a == b :
        a.AlsoLocal == b.AlsoLocal && a.Output.Kind == b.Output.Kind && (a.Output.Kind == MixerOutputKind.Speaker
            ? MixerRoutes.SpeakerKey(a.Output.Id!) == MixerRoutes.SpeakerKey(b.Output.Id!)
            : string.Equals(a.Output.Id, b.Output.Id, StringComparison.OrdinalIgnoreCase));

    internal static string Name(AppConfig config, MixerRoute route, IReadOnlyList<AirPlayDevice>? scan = null)
    {
        if (route.Output.Kind == MixerOutputKind.Local) return L.T("本机");
        var resolved = MixerDestinationResolver.Resolve(config, route, scan);
        string name = resolved.Error == MixerRoutingError.NoSelection ? L.T("还没有选择音箱") : resolved.Name;
        if (resolved.Error == MixerRoutingError.MissingDestination) return L.T("去处已删除，请重新选择");
        if (route.Output.Kind == MixerOutputKind.Current) name = L.F("当前输出：{0}", name);
        return route.AlsoLocal ? L.F("{0} + {1}", name, L.T("本机")) : name;
    }

    internal static string Error(MixerRoutingError error) => error switch
    {
        MixerRoutingError.MissingDestination => L.T("去处已删除，请重新选择"),
        MixerRoutingError.DuplicateLocal => L.T("本机副本会和此组的本机输出重复播放。"),
        MixerRoutingError.NoSelection => L.T("还没有选择音箱"),
        _ => L.T("去处设置无效或版本不受支持。"),
    };

    internal static string Error(MixerEditOutcome outcome, AppConfig config)
    {
        if (outcome.Error != MixerRoutingError.None) return Error(outcome.Error);
        if (outcome.Conflict is { } conflict)
        {
            if (conflict.Error == MixerLeaseError.UnconfirmedIdentity) return L.T("请先确认这些输出是否来自同一台实体设备。");
            string owner = config.SavedOutputGroups.FirstOrDefault(g => "group:" + g.Id.ToLowerInvariant() == conflict.Owner)?.Name ??
                MixerDestinationResolver.Resolve(config, MixerRoute.Current).Name;
            return L.F("此音箱已用于「{0}」；先停止该去处，或选择同一去处。", owner.Length > 0 ? owner : L.T("其他去处"));
        }
        return outcome.Result == MixerEditResult.SaveFailed ? L.T("保存失败，原设置已保留。") :
            outcome.Result == MixerEditResult.InvalidDestination ? L.T("去处已删除，请重新选择") : L.T("去处设置无效或版本不受支持。");
    }
}
