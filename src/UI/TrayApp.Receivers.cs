using HomePodCast.Net;

namespace HomePodCast.UI;

internal sealed partial class TrayApp
{
    private object? _rememberedReceiverSession;

    internal ReceiverLatencySelection? ReceiverSelection
    {
        get
        {
            if (Config.DeviceId is not { } id) return null;
            var device = Controller.ReceiverLookup?.Invoke(id);
            if (GroupPlan.FromConfig(Config) != null && (device == null || device.Kind is ReceiverKind.HomePod or ReceiverKind.AppleTv)) return null;
            var settings = Config.ReceiverSettings.GetValueOrDefault(RaopPassword.Key(id));
            bool live = Controller.State == StreamState.Streaming;
            return ReceiverLatencySelection.Create(device, settings, live ? Controller.ActiveProtocol : null,
                live ? Controller.ReceiverSession?.ArrivalToRenderMs ?? Controller.Client?.ArrivalToRenderMs : null);
        }
    }

    internal void SetReceiverLatency(int milliseconds)
    {
        if (ReceiverSelection is not { } selection || selection.Policy.MinimumMs > selection.Policy.MaximumMs) return;
        string key = RaopPassword.Key(selection.Device.DeviceId);
        if (!Config.ReceiverSettings.TryGetValue(key, out var settings))
            Config.ReceiverSettings[key] = settings = ReceiverPreferences.Of(selection.Device);
        selection.Save(settings, milliseconds);
        Config.Save();
        _form.UpdateState(); RaiseStateChanged();
        if (_wantConnected) Connect(ConnectTrigger.Reconnect);
    }

    private void RememberReceiverConnection()
    {
        if (Controller.State != StreamState.Streaming || ReceiverSelection is not { } selection
            || ReferenceEquals(_rememberedReceiverSession, Controller.AudioSessionIdentity)) return;
        _rememberedReceiverSession = Controller.AudioSessionIdentity;
        string key = RaopPassword.Key(selection.Device.DeviceId);
        if (!Config.ReceiverSettings.TryGetValue(key, out var settings))
            Config.ReceiverSettings[key] = settings = ReceiverPreferences.Of(selection.Device);
        settings.LastSuccessfulProtocol = selection.Protocol;
        if ((Controller.ReceiverSession?.ArrivalToRenderMs ?? Controller.Client?.ArrivalToRenderMs) is { } processing)
            settings.ArrivalToRenderMs = processing;
        bool notice = selection.TakeCompatibilityNotice(settings);
        Config.Save();
        if (notice) _tray.ShowBalloonTip(8000, L.T("AirPlay 兼容模式（实验性）"), // experimental since 1.1
            L.T("兼容模式延迟约 2 秒，适合听音乐"), ToolTipIcon.Info);
    }
}
