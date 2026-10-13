using HomePodCast.Audio;

namespace HomePodCast.UI;

// Microphone, reverb and equalizers (MicEffects) and their place in the send path:
// mic mix-in → output EQ (all sound) → night-mode compressor.
internal sealed partial class TrayApp
{
    private MicEffects? _fx;

    /// <summary>The microphone and effects engine shared by the send path and the 麦克风与音效 page.</summary>
    public MicEffects Fx => _fx!;

    /// <summary>Windows' privacy settings refused the microphone, and the user was told (once per run).</summary>
    private bool _micDeniedShown;

    /// <summary>Called once from the constructor, after InitSound.</summary>
    private void InitEffects()
    {
        _fx = new MicEffects(Config.Effects);
        Controller.Effects.Insert(0, _fx.OutputEq);
        Controller.Effects.Insert(0, new MixIn(_fx.HomePodSource));
        _fx.Changed += () => _ui.Post(_ => OnMicChanged(), null);
        ApplyNightEq();
    }

    /// <summary>
    /// The mic opened, failed or closed (UI thread). Refused by the privacy settings: say so once, with the way to the
    /// switches; 麦克风与音效 keeps saying it under the mic for as long as it lasts.
    /// </summary>
    private void OnMicChanged()
    {
        if (_quitting || _micDeniedShown || _fx?.Mic is not { AccessDenied: true }) return;
        _micDeniedShown = true;
        var answer = MessageBox.Show(MicDeniedText(AppEnvironment.IsPackaged), L.T("无法使用麦克风"),
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (answer == DialogResult.Yes) AppEnvironment.OpenUri(Audio.MicCapture.PrivacySettingsUri);
    }

    /// <summary>
    /// Where the switch is: the Store version has its own in the list of apps; an unpackaged app is one of the desktop
    /// apps, which share one switch.
    /// </summary>
    internal static string MicDeniedText(bool packaged) => packaged
        ? L.T("Windows 的隐私设置不允许 HomePodCast 使用麦克风。\n\n" +
              "请在 Windows 设置 → 隐私和安全性 → 麦克风 里打开“麦克风访问权限”，并在应用列表里打开 HomePodCast，然后把麦克风关掉再打开。\n\n" +
              "点「是」打开 Windows 设置。")
        : L.T("Windows 的隐私设置不允许 HomePodCast 使用麦克风。\n\n" +
              "请在 Windows 设置 → 隐私和安全性 → 麦克风 里打开“麦克风访问权限”和“允许桌面应用访问你的麦克风”，然后把麦克风关掉再打开。\n\n" +
              "点「是」打开 Windows 设置。");

    /// <summary>Night mode also turns on 「减弱低音」 on the output EQ, like the HomePod's own "Reduce Bass".</summary>
    private void ApplyNightEq()
    {
        if (_fx != null) _fx.OutputPresetOverride = Config.NightMode ? EqPresets.Id(EqPreset.ReduceBass) : null;
    }

    /// <summary>
    /// Turn the microphone on or off (tray flyout) with the rules of the switch on the 麦克风与音效 page, which
    /// asks first when the voice would also go to loudspeakers. Never saved. Returns whether it is on now.
    /// </summary>
    public bool SetMicOn(bool on)
    {
        bool result = _form.SetMicOn(on);
        RaiseStateChanged();
        return result;
    }

    private void DisposeEffects() => _fx?.Dispose();
}
