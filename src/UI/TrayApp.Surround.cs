namespace HomePodCast.UI;

// experimental since 1.4
// 首页 →「5.1 环绕声」: one app-wide switch. StreamController.Surround.cs decides per connect attempt whether a single
// HomePod gets 5.1 (SurroundFor, wired in TrayApp.Mixer.cs); everything else stays stereo.
internal sealed partial class TrayApp
{
    /// <summary>
    /// Saved at once. A running connection reconnects: the mixer restarts only the destinations whose signature changed
    /// (a single speaker; groups never use 5.1, so they keep playing), and that restart decides between 5.1 and stereo again.
    /// </summary>
    public void SetSurround51(bool on)
    {
        if (Config.Surround51 == on) return;
        Config.Surround51 = on;
        Config.Save();
        Log.Info($"5.1 surround {(on ? "on" : "off")}");
        _form.ShowSoundOptions();
        RaiseStateChanged();
        if (_wantConnected) Connect(ConnectTrigger.Reconnect);
    }
}
