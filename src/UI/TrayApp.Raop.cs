using HomePodCast.Net;
using HomePodCast.UI.Controls;

namespace HomePodCast.UI;

internal sealed partial class TrayApp
{
    private bool _raopDialog;
    private void InitRaop()
    {
        Controller.ReceiverLookup = id => LastScan.FirstOrDefault(d => RaopPassword.Key(d.DeviceId) == RaopPassword.Key(id))
            ?? Config.ReceiverSettings.GetValueOrDefault(RaopPassword.Key(id))?.Device(id)
            ?? Config.ManualDevices.Select(d => d.ToDevice()).FirstOrDefault(d => d != null && RaopPassword.Key(d.DeviceId) == RaopPassword.Key(id));
        Controller.ReceiverSettings = id => Config.ReceiverSettings.GetValueOrDefault(RaopPassword.Key(id));
        Controller.ReceiverProtocolConnected += (id, protocol, processing) => _ui.Post(_ =>
        {
            if (_quitting) return;
            string key = RaopPassword.Key(id);
            if (!Config.ReceiverSettings.TryGetValue(key, out var settings))
            {
                if (Controller.ReceiverLookup?.Invoke(id) is not { } device) return;
                Config.ReceiverSettings[key] = settings = ReceiverPreferences.Of(device);
            }
            settings.LastSuccessfulProtocol = protocol;
            if (processing != null) settings.ArrivalToRenderMs = processing;
            Config.Save();
        }, null);
        Controller.CompatibilityLatency = id => Config.ReceiverSettings.GetValueOrDefault(RaopPassword.Key(id))?.CompatibilityLatencyMs ?? 2000;
        Controller.ReceiverPassword = id => RaopPassword.Unprotect(id, Config.RaopPasswordsProtected.GetValueOrDefault(RaopPassword.Key(id)));
        Controller.RaopPasswordRequired += request => _ui.Post(_ =>
        {
            if (!CanShowRaopDialog(request) || !PairingPolicy.OffersPinPairing(request.Trigger)) return;
            _raopDialog = true;
            try
            {
                using var dialog = new RaopPasswordDialog(request.Device.Name);
                if (dialog.ShowDialog(_form.Visible ? _form : null) != DialogResult.OK || _quitting) return;
                Config.RaopPasswordsProtected[RaopPassword.Key(request.Device.DeviceId)] = RaopPassword.Protect(request.Device.DeviceId, dialog.Password);
                Config.Save();
                Connect(request.Trigger);
            }
            finally { _raopDialog = false; }
        }, null);
        Controller.RaopTakeoverRequired += request => _ui.Post(_ =>
        {
            if (!CanShowRaopDialog(request)) return;
            _raopDialog = true;
            try
            {
                if (TakeoverDialog.Confirm(_form.Visible ? _form : null, request.Device.Name) && !_quitting)
                    Connect(ConnectTrigger.TakeOver);
            }
            finally { _raopDialog = false; }
        }, null);
    }

    public void SetReceiverProtocol(string id, ReceiverProtocol protocol)
    {
        if (Controller.ReceiverLookup?.Invoke(id) is not { } device || device.Kind is ReceiverKind.HomePod or ReceiverKind.AppleTv) return;
        string key = RaopPassword.Key(id);
        if (!Config.ReceiverSettings.TryGetValue(key, out var settings))
            Config.ReceiverSettings[key] = settings = ReceiverPreferences.Of(device);
        if (settings.ProtocolOverride == protocol) return;
        settings.ProtocolOverride = protocol;
        Config.Save();
        _form.UpdateState(); RaiseStateChanged();
        // Speaker info can inspect a receiver other than the one currently playing. Only that receiver's next
        // connection should use its new preference; editing it must not interrupt the current speaker.
        if (_wantConnected && Config.DeviceId is { } active && RaopPassword.Key(active) == key) Connect(ConnectTrigger.Reconnect);
    }

    private bool CanShowRaopDialog(RaopInteraction request) => !_quitting && !_raopDialog
        && (request.GroupGeneration != null ? Controller.MatchesRaopGroupInteraction(request)
            : Config.DeviceId != null && RaopPassword.Key(Config.DeviceId) == RaopPassword.Key(request.Device.DeviceId));
}

internal sealed class RaopPasswordDialog : FluentDialog
{
    private readonly PasswordBox _password = new(L.T("AirPlay 密码"));
    public string Password => _password.Password;
    public RaopPasswordDialog(string speaker) : base(L.T("AirPlay 设备密码（实验性）")) // experimental since 1.1
    {
        var connect = new FluentButton(L.T("连接"), ButtonKind.Primary) { DialogResult = DialogResult.OK, Enabled = false };
        var cancel = new FluentButton(L.T("取消")) { DialogResult = DialogResult.Cancel };
        Body.Controls.AddRange([
            Ui.Note(L.F("为「{0}」填写 AirPlay 密码。密码只在这台电脑上加密保存。", speaker)), _password,
            ButtonRow(cancel, connect),
        ]);
        _password.PasswordChanged += (_, _) => connect.Enabled = _password.Password.Length > 0;
        _password.Submitted += (_, _) => { if (connect.Enabled) DialogResult = DialogResult.OK; };
        AcceptButton = connect; CancelButton = cancel;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) _password.Password = "";
        base.Dispose(disposing);
    }
}
