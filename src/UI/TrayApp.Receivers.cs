using HomePodCast.Net;

namespace HomePodCast.UI;

internal sealed partial class TrayApp
{
    private object? _rememberedReceiverSession;

    internal ReceiverLatencySelection? ReceiverSelection
    {
        get
        {
            var config = PlaybackConfig;
            if (config.DeviceId is not { } id) return null;
            var device = Controller.ReceiverLookup?.Invoke(id);
            if (GroupPlan.FromConfig(config) != null) return null;
            var settings = Config.ReceiverSettings.GetValueOrDefault(RaopPassword.Key(id));
            bool live = Controller.State == StreamState.Streaming;
            return ReceiverLatencySelection.Create(device, settings, live ? Controller.ActiveProtocol : null,
                live ? Controller.ReceiverSession?.ArrivalToRenderMs ?? Controller.Client?.ArrivalToRenderMs : null);
        }
    }

    internal async void SetReceiverLatency(int milliseconds)
    {
        if (ReceiverSelection is not { } selection || selection.Policy.MinimumMs > selection.Policy.MaximumMs) return;
        if (Mixer != null && SelectedMixerKey is { } destination)
        {
            await ApplyMixerDelayAsync(destination, new(PlaybackConfig.Scene, milliseconds, PlaybackConfig.FifoTargetMs, PlaybackConfig.Volume));
            _form.UpdateState(); RaiseStateChanged(); return;
        }
        var settings = Config.ReceiverPreferencesFor(RaopPassword.Key(selection.Device.DeviceId), () => ReceiverPreferences.Of(selection.Device));
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
        var settings = Config.ReceiverPreferencesFor(RaopPassword.Key(selection.Device.DeviceId), () => ReceiverPreferences.Of(selection.Device));
        settings.LastSuccessfulProtocol = selection.Protocol;
        if ((Controller.ReceiverSession?.ArrivalToRenderMs ?? Controller.Client?.ArrivalToRenderMs) is { } processing)
            settings.ArrivalToRenderMs = processing;
        bool notice = selection.TakeCompatibilityNotice(settings);
        Config.TrySave(); // part of OnControllerChanged, which has to go on
        if (notice) _tray.ShowBalloonTip(8000, L.T("AirPlay 兼容模式（实验性）"), // experimental since 1.1
            L.T("兼容模式延迟约 2 秒，适合听音乐"), ToolTipIcon.Info);
    }
}
