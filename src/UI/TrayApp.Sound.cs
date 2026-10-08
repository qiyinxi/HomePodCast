using HomePodCast.Audio;

namespace HomePodCast.UI;

// Scenes, night mode, volume cap, speaker mute, global hotkeys and the keyboard volume keys (VolumeKeyMode).
internal sealed partial class TrayApp
{
    private const double HotkeyVolumeStep = 5;

    /// <summary>Volume keys, hotkeys and the Windows volume move the speaker at once; the config is saved this long after the last step.</summary>
    private const int VolumeSaveDelayMs = 400;

    private GlobalHotkeys? _hotkeys;
    private VolumeKeyForwarder? _forwarder;   // Windows endpoint: FollowWindows, WhenWindowsMuted, output device present
    private VolumeKeyHook? _keyHook;          // keyboard hook: WhileStreaming (and no output device)
    private VolumeOsd? _osd;
    private TimerDebounce? _volumeSave;
    private double? _pendingVolume;
    private VolumeKeyRoute _route;

    /// <summary>Configured hotkeys that could not be registered (another program holds them, or unreadable).</summary>
    public HashSet<HotkeyAction> UnavailableHotkeys { get; } = [];

    /// <summary>Called once from the constructor, before the tray icon and the main window.</summary>
    private void InitSound()
    {
        bool dirty = Scenes.Reconcile(Config);
        int cap = VolumeLimit.NormalizeCap(Config.VolumeCapPercent);
        if (cap != Config.VolumeCapPercent || Config.Volume > cap)
        {
            Config.VolumeCapPercent = cap;
            if (Config.Volume > cap) Config.Volume = cap;
            dirty = true;
        }
        if (dirty) Config.Save();
        Controller.SetVolumeCap(cap);
        Controller.NightMode.Enabled = Config.NightMode;

        _hotkeys = new GlobalHotkeys();
        _hotkeys.Pressed += OnHotkey;
        ApplyHotkeys();

        _volumeSave = new TimerDebounce(VolumeSaveDelayMs);
        _volumeSave.Tick += SavePendingVolume;
        try
        {
            _forwarder = new VolumeKeyForwarder(); // watches the default output; takes a Func<string?> endpoint id for another one
            _forwarder.VolumeStep += pct => _ui.Post(_ => NudgeVolume(pct), null);
            _forwarder.MuteToggled += () => _ui.Post(_ => ToggleSpeakerMute(), null);
            _forwarder.WindowsChanged += (from, to) => _ui.Post(_ => FollowWindows(from, to), null);
            _forwarder.OutputDeviceChanged += _ => _ui.Post(_ => UpdateVolumeKeys(), null);
        }
        catch (Exception ex)
        {
            Log.Warn($"Windows volume watching unavailable: {ex.Message}");
        }
        _keyHook = new VolumeKeyHook(command => _ui.Post(_ => OnVolumeKey(command), null));
        UpdateVolumeKeys();
    }

    private void DisposeSound()
    {
        SavePendingVolume();
        _hotkeys?.Dispose();
        _keyHook?.Dispose();
        _forwarder?.Dispose();
        _osd?.Dispose();
        _volumeSave?.Dispose();
    }

    // ---------------------------------------------------------------- actions

    /// <summary>Switch scene; the main window owns the reconnect debounce (same rule as the latency slider).</summary>
    public void SelectScene(Scene scene) => _form.SelectScene(scene);

    public void SetNightMode(bool on)
    {
        Config.NightMode = on;
        Config.Save();
        Controller.NightMode.Enabled = on;
        ApplyNightEq();
        Log.Info($"night mode {(on ? "on" : "off")}");
        _form.ShowSoundOptions();
        RaiseStateChanged();
    }

    public void SetVolumeCap(int cap)
    {
        cap = VolumeLimit.NormalizeCap(cap);
        Config.VolumeCapPercent = cap;
        if (Config.Volume > cap) Config.Volume = cap;
        Config.Save();
        Controller.SetVolumeCap(cap);
        RaiseStateChanged();
    }

    public void SetSpeakerMuted(bool muted)
    {
        SavePendingVolume(); // saved first, so a late save can't unmute
        Controller.SetMuted(muted);
        _form.ShowSoundOptions();
        RaiseStateChanged();
    }

    public void ToggleSpeakerMute() => SetSpeakerMuted(!Controller.Muted);

    /// <summary>Step the speaker volume (hotkeys, volume keys); unmutes and respects the cap.</summary>
    public void NudgeVolume(double delta)
    {
        if ((Controller.Volume ?? Config.Volume) is not { } current) return; // speaker volume not known yet
        SetVolumeLive(current + delta);
    }

    /// <summary>
    /// A volume from the keys, hotkeys or the Windows volume: sent at once like a slider drag (PreviewVolume, which
    /// also ends a mute), saved with SetVolume once the steps stop, so held keys don't write the config 30 times a second.
    /// </summary>
    private void SetVolumeLive(double percent)
    {
        percent = VolumeLimit.Clamp(percent, Config.VolumeCapPercent);
        PreviewVolume(percent);
        _pendingVolume = percent;
        _volumeSave?.Restart();
        _form.ShowVolume(percent);
        _form.ShowSoundOptions();
        RaiseStateChanged();
    }

    private void SavePendingVolume()
    {
        _volumeSave?.Stop();
        if (_pendingVolume is not { } percent) return;
        _pendingVolume = null;
        SetVolume(Controller.Volume ?? percent); // what the speaker has now (a slider may have moved it since)
    }

    // ---------------------------------------------------------------- volume keys

    /// <summary>Windows has an output device whose volume can be followed (true until first checked).</summary>
    public bool OutputDevicePresent => _forwarder?.OutputDevicePresent ?? false;

    public void SetVolumeKeyMode(VolumeKeyMode mode)
    {
        if (Config.VolumeKeys == mode) return;
        Config.VolumeKeys = mode;
        Config.Save();
        Log.Info($"volume keys: {mode}");
        UpdateVolumeKeys();
    }

    /// <summary>Hook in or out, start or stop watching Windows: after a mode change, the stream starting or stopping,
    /// or the output device going or coming back. UI thread.</summary>
    private void UpdateVolumeKeys()
    {
        bool streaming = Controller.State == StreamState.Streaming;
        bool device = OutputDevicePresent;
        var mode = Config.VolumeKeys;
        var route = VolumeKeyRules.RouteFor(mode, streaming, device);
        if (_forwarder != null) _forwarder.Mode = VolumeKeyRules.EndpointModeFor(route);
        if (_keyHook != null)
        {
            _keyHook.SetState(mode, streaming, device);
            if (route == VolumeKeyRoute.Intercept) _keyHook.Install();
            else _keyHook.Uninstall();
        }
        if (route != _route) Log.Info($"volume keys: {route} ({mode}, {(streaming ? "streaming" : "not streaming")}, " +
                                      $"{(device ? "output device present" : "no output device")})");
        _route = route;
        _form?.ShowVolumeKeys();
    }

    /// <summary>A volume key taken by the hook: one step (2 %, unmutes) or the speaker-mute toggle, and the OSD.</summary>
    private void OnVolumeKey(VolumeKeyCommand command)
    {
        if (command == VolumeKeyCommand.Mute) ToggleSpeakerMute();
        else NudgeVolume(VolumeKeyRules.StepFor(command));
        if (VolumeOsd.Allowed()) ShowVolumeOsd(); // not over an exclusive full-screen game or a presentation
    }

    /// <summary>FollowWindows: the Windows volume or mute changed (not by us); the HomePod follows within the cap.</summary>
    private void FollowWindows(Audio.EndpointState from, Audio.EndpointState to)
    {
        if (_route != VolumeKeyRoute.Follow) return; // stopped streaming or switched mode meanwhile
        var action = VolumeKeyRules.Follow(from, to, Config.VolumeCapPercent);
        if (action.IsNone) return;
        Log.Debug($"Windows volume {(to.Muted ? "muted" : $"{to.Level:P0}")} → HomePod " +
                  (action.Percent is { } p ? $"{p:0.#}%" : action.Mute == true ? "muted" : "unmuted"));
        if (action.Percent is { } percent) SetVolumeLive(percent); // also ends a mute
        else if (action.Mute is { } mute && mute != Controller.Muted) SetSpeakerMuted(mute);
    }

    /// <summary>The HomePod volume (or mute) on the OSD; it closes itself after fading out, and the next one is made new.</summary>
    private void ShowVolumeOsd(double? fallback = null, bool? muted = null, int? holdMs = null)
    {
        try
        {
            if (_osd is not { IsDisposed: false }) _osd = new VolumeOsd();
            if (holdMs is { } hold) _osd.HoldMs = hold;
            _osd.Present(Controller.Volume ?? Config.Volume ?? fallback, muted ?? Controller.Muted, Config.VolumeCapPercent);
        }
        catch (Exception ex)
        {
            Log.Warn($"volume OSD: {ex.Message}");
        }
    }

    /// <summary><c>gui --osd</c> / <c>--osd-muted</c>: the OSD for a minute, for checking its look (sends nothing).</summary>
    private void ShowTestOsd(bool muted) => ShowVolumeOsd(fallback: 42, muted: muted, holdMs: 60_000);

    // ---------------------------------------------------------------- hotkeys

    private void OnHotkey(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.VolumeUp: NudgeVolume(+HotkeyVolumeStep); break;
            case HotkeyAction.VolumeDown: NudgeVolume(-HotkeyVolumeStep); break;
            case HotkeyAction.Mute: ToggleSpeakerMute(); break;
            case HotkeyAction.NextScene:
                var next = Scenes.Next(Config.Scene);
                SelectScene(next);
                _tray.ShowBalloonTip(2000, L.T("HomePod 音响"),
                    L.F("场景：{0}（{1} ms）", Scenes.Name(next), Scenes.LatencyMs(next, Scenes.CustomMs(Config))), ToolTipIcon.None);
                break;
        }
    }

    /// <summary>(Re)register every hotkey from the config and remember which ones are taken.</summary>
    public void ApplyHotkeys()
    {
        if (_hotkeys == null) return;
        _hotkeys.UnregisterAll();
        UnavailableHotkeys.Clear();
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            var text = Hotkey.ConfigText(Config, action);
            if (text.Length == 0) continue; // switched off
            if (!Hotkey.TryParse(text, out var key) || !_hotkeys.Register(action, key)) UnavailableHotkeys.Add(action);
        }
        _form?.ShowHotkeyStatus(UnavailableHotkeys.Count);
    }

    /// <summary>Let go of all hotkeys (while the hotkey dialog records new ones); ApplyHotkeys takes them back.</summary>
    public void SuspendHotkeys() => _hotkeys?.UnregisterAll();

    public bool ProbeHotkey(Hotkey key) => _hotkeys?.Probe(key) ?? false;

    public void ShowHotkeys(IWin32Window owner)
    {
        using var dialog = new HotkeysForm(this);
        dialog.ShowDialog(owner);
    }
}
