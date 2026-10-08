using HomePodCast.Audio;

namespace HomePodCast.UI;

// Scenes, night mode, volume cap, speaker mute, global hotkeys and volume-key forwarding.
internal sealed partial class TrayApp
{
    private const double HotkeyVolumeStep = 5;

    private readonly ToolStripMenuItem _sceneMenu = new();
    private readonly ToolStripMenuItem _nightItem = new(L.T("夜间模式"));
    private readonly ToolStripMenuItem _muteItem = new(L.T("HomePod 静音"));
    private GlobalHotkeys? _hotkeys;
    private VolumeKeyForwarder? _forwarder;

    /// <summary>Configured hotkeys that could not be registered (another program holds them, or unreadable).</summary>
    public HashSet<HotkeyAction> UnavailableHotkeys { get; } = [];

    /// <summary>Called once from the constructor, after the tray menu is built and before the main window.</summary>
    private void InitSound(ContextMenuStrip menu)
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

        foreach (var scene in Scenes.All)
        {
            var item = new ToolStripMenuItem { Tag = scene };
            item.Click += (_, _) => SelectScene(scene);
            _sceneMenu.DropDownItems.Add(item);
        }
        _nightItem.Click += (_, _) => SetNightMode(!Config.NightMode);
        _muteItem.Click += (_, _) => ToggleSpeakerMute();
        int at = menu.Items.IndexOf(_toggleItem) + 1;
        ToolStripItem[] items = [new ToolStripSeparator(), _sceneMenu, _nightItem, _muteItem, new ToolStripSeparator()];
        for (int i = 0; i < items.Length; i++) menu.Items.Insert(at + i, items[i]);
        menu.Opening += (_, _) => RefreshSoundMenu();
        RefreshSoundMenu();

        _hotkeys = new GlobalHotkeys();
        _hotkeys.Pressed += OnHotkey;
        ApplyHotkeys();

        try
        {
            _forwarder = new VolumeKeyForwarder();
            _forwarder.VolumeStep += pct => _ui.Post(_ => NudgeVolume(pct), null);
            _forwarder.MuteToggled += () => _ui.Post(_ => ToggleSpeakerMute(), null);
            Controller.Changed += UpdateForwarder;
            UpdateForwarder();
        }
        catch (Exception ex)
        {
            Log.Warn($"volume-key forwarding unavailable: {ex.Message}");
        }
    }

    private void DisposeSound()
    {
        Controller.Changed -= UpdateForwarder;
        _hotkeys?.Dispose();
        _forwarder?.Dispose();
    }

    private void RefreshSoundMenu()
    {
        foreach (ToolStripMenuItem item in _sceneMenu.DropDownItems)
        {
            var scene = (Scene)item.Tag!;
            item.Text = L.F("{0}（{1} ms）", Scenes.Name(scene), Scenes.LatencyMs(scene, Scenes.CustomMs(Config)));
            item.Checked = scene == Config.Scene;
        }
        _sceneMenu.Text = L.F("场景：{0}", Scenes.Name(Config.Scene));
        _nightItem.Checked = Config.NightMode;
        _muteItem.Checked = Controller.Muted;
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
    }

    public void SetVolumeCap(int cap)
    {
        cap = VolumeLimit.NormalizeCap(cap);
        Config.VolumeCapPercent = cap;
        if (Config.Volume > cap) Config.Volume = cap;
        Config.Save();
        Controller.SetVolumeCap(cap);
    }

    public void SetSpeakerMuted(bool muted)
    {
        Controller.SetMuted(muted);
        _form.ShowSoundOptions();
    }

    public void ToggleSpeakerMute() => SetSpeakerMuted(!Controller.Muted);

    /// <summary>Step the speaker volume (hotkeys, forwarded volume keys); unmutes and respects the cap.</summary>
    public void NudgeVolume(double delta)
    {
        if ((Controller.Volume ?? Config.Volume) is not { } current) return; // speaker volume not known yet
        SetVolume(Math.Clamp(current + delta, 0, Config.VolumeCapPercent));
        _form.ShowVolume(Controller.Volume ?? current);
        _form.ShowSoundOptions();
    }

    public void SetForwardVolumeKeys(bool on)
    {
        Config.ForwardVolumeKeys = on;
        Config.Save();
        UpdateForwarder();
    }

    private void UpdateForwarder()
    {
        if (_forwarder != null) _forwarder.Active = Config.ForwardVolumeKeys && Controller.State == StreamState.Streaming;
    }

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
