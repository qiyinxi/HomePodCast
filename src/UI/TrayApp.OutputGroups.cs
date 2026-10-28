using HomePodCast.Net;

namespace HomePodCast.UI;

internal sealed partial class TrayApp
{
    internal bool HasOutputSelection => HasConfiguredOutputs(Config);

    internal static bool ShouldConnectOnStartup(AppConfig config, bool? resume) =>
        HasConfiguredOutputs(config) && (resume ?? config.AutoConnect);

    private static bool HasConfiguredOutputs(AppConfig config)
    {
        if (config.MixerRouting != null && MixerRoutes.Read(config).Settings is { } routing &&
            new[] { routing.Default }.Concat(routing.Apps.Values).Any(r => r.Output.Kind is MixerOutputKind.Speaker or MixerOutputKind.Group &&
                MixerDestinationResolver.Resolve(config, r).Available)) return true;
        var plan = GroupPlan.FromConfig(config);
        return plan == null ? config.DeviceId != null : plan.Kind == GroupKind.StereoPair
            || plan.Members.Count + plan.LocalOutputs.Count + plan.DlnaOutputs.Count > 0;
    }

    internal void SelectOutputGroup(string id)
    {
        var group = Config.SavedOutputGroups.FirstOrDefault(g => g.Id == id);
        if (group == null) return;
        if (Mixer != null)
        {
            var projection = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(System.Text.Json.JsonSerializer.Serialize(Config))!;
            OutputGroups.Apply(projection, group.Settings); projection.SelectedOutputGroupId = group.Id;
            if (Mixer.ValidateConfiguration(projection).Result != MixerEditResult.Applied) return;
        }
        _form.Flush(); SavePendingVolume();
        OutputGroups.Apply(Config, group.Settings);
        Config.SelectedOutputGroupId = group.Id;
        Config.Save(); _form.ReloadConfiguration(); RaiseStateChanged();
        if (_wantConnected) Connect(ConnectTrigger.Manual);
    }

    private bool CheckGroupMemberBeforeManualConnect(GroupPlan? plan, string id)
    {
        if (plan == null || plan.Kind == GroupKind.StereoPair) return true;
        if (plan.RequiredOutputs.FirstOrDefault(p => GroupPlan.Key(p.Key) == GroupPlan.Key(id)) is { Key: not null } saved) return saved.Value;
        var device = Controller.ReceiverLookup?.Invoke(id);
        return device?.Kind is not (ReceiverKind.AppleAirPlay1 or ReceiverKind.ThirdPartyAirPlay1)
            && Config.ReceiverSettings.GetValueOrDefault(RaopPassword.Key(id))?.ProtocolOverride != ReceiverProtocol.Raop;
    }

    internal void SelectSingleOutput(AirPlayDevice device)
    {
        string before = GroupPanel.PlayLayout(Config);
        Config.GroupCaptureDeviceIds = StereoPairs.IsPairId(device.DeviceId) ? null : [device.DeviceId];
        Config.GroupCaptureLocalDeviceIds = []; Config.GroupCaptureDlnaUdns = [];
        Config.MultiRoomDeviceId = Config.MultiRoomDeviceName = Config.MultiRoomHost = null;
        Config.GroupDelayMs = null; Config.SelectedOutputGroupId = null;
        bool same = GroupPlan.Key(Config.DeviceId ?? "") == GroupPlan.Key(device.DeviceId);
        SelectDevice(device);
        if (same)
        {
            Config.Save(); _form.ReloadConfiguration(); RaiseStateChanged();
            if (_wantConnected && before != GroupPanel.PlayLayout(Config)) Connect(ConnectTrigger.Manual);
        }
    }
}
