namespace HomePodCast.UI;

// Scene presets, speaker mute, night mode, volume cap and the hotkeys button.
internal sealed partial class MainForm
{
    private readonly FlowLayoutPanel _scenes = new() { AutoSize = true, WrapContents = true, Margin = new Padding(3, 2, 0, 2) };
    private readonly Label _movieHint = new() { AutoSize = true, ForeColor = Color.DimGray, Visible = false };
    private readonly CheckBox _speakerMute = new() { Text = L.T("静音"), AutoSize = true };
    private readonly CheckBox _night = new() { Text = L.T("夜间模式"), AutoSize = true };
    private readonly NumericUpDown _cap = new() { Minimum = VolumeLimit.MinCap, Maximum = 100, Increment = 5, Width = 52 };
    private readonly Button _hotkeysButton = new() { Text = L.T("快捷键…"), AutoSize = true };
    private readonly ToolTip _soundTips = new();
    private readonly System.Windows.Forms.Timer _sceneDebounce = new() { Interval = 1000 };
    private int _pendingSceneMs;

    /// <summary>Adds three rows under the volume row (mute / night mode / cap, scene, movie hint) and the hotkeys button.</summary>
    private void BuildSoundRows(TableLayoutPanel layout)
    {
        const int at = 4, added = 3;
        foreach (Control c in layout.Controls)
        {
            var p = layout.GetCellPosition(c);
            if (p.Row >= at) layout.SetCellPosition(c, new TableLayoutPanelCellPosition(p.Column, p.Row + added));
        }
        for (int i = 0; i < added; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // Wrappable, so the table does not treat its full width as a minimum and widen the window (it fits on one line).
        var sound = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 6) };
        _speakerMute.Margin = new Padding(10, 3, 3, 3);
        _night.Margin = new Padding(14, 3, 3, 3);
        var capLabel = new Label { Text = L.T("音量上限"), AutoSize = true, Margin = new Padding(14, 5, 2, 0) };
        _cap.Margin = new Padding(2, 3, 3, 0);
        sound.Controls.AddRange([_speakerMute, _night, capLabel, _cap]);
        layout.Controls.Add(sound, 1, at);
        layout.SetColumnSpan(sound, 2);

        foreach (var scene in Scenes.All)
        {
            var b = new RadioButton
            {
                Text = Scenes.Name(scene),
                Tag = scene,
                Appearance = Appearance.Button,
                AutoSize = true,
                MinimumSize = new Size(60, 28),
                TextAlign = ContentAlignment.MiddleCenter,
                Margin = new Padding(0, 0, 4, 0),
            };
            b.CheckedChanged += (_, _) => { if (b.Checked && !_loading) SelectScene(scene); };
            _scenes.Controls.Add(b);
        }
        layout.Controls.Add(Caption(L.T("场景")), 0, at + 1);
        layout.Controls.Add(_scenes, 1, at + 1);
        layout.SetColumnSpan(_scenes, 2);

        _movieHint.MaximumSize = new Size(330, 0);
        _movieHint.Margin = new Padding(3, 2, 3, 4);
        layout.Controls.Add(_movieHint, 1, at + 2);
        layout.SetColumnSpan(_movieHint, 2);

        _hotkeysButton.Margin = new Padding(10, 3, 3, 3);
        _mixer.Parent?.Controls.Add(_hotkeysButton);

        _soundTips.SetToolTip(_speakerMute, L.T("让 HomePod 静音，不断开连接"));
        _soundTips.SetToolTip(_night, L.T("压缩动态范围：爆炸、枪声变小，对白、脚步声变大，适合夜里小音量"));
        _soundTips.SetToolTip(_cap, L.T("HomePod 音量不会超过这个值（连接、重连、快捷键都一样）。100 = 不限制"));

        _sceneDebounce.Tick += (_, _) =>
        {
            _sceneDebounce.Stop();
            _app.SetLatency(_pendingSceneMs);
        };
        _latency.ValueChanged += (_, _) =>
        {
            if (_loading || _app.Config.Scene == Scene.Custom) return;
            // Moving the slider (or ±) by hand leaves the preset; the slider's own debounce reconnects.
            _app.Config.Scene = Scene.Custom;
            _app.Config.Save();
            _sceneDebounce.Stop();
            ShowSoundOptions();
        };
        _volume.ValueChanged += (_, _) =>
        {
            int cap = _app.Config.VolumeCapPercent;
            if (_volume.Value > cap) _volume.Value = cap; // the thumb stops at the cap
        };
        _speakerMute.CheckedChanged += (_, _) => { if (!_loading) _app.SetSpeakerMuted(_speakerMute.Checked); };
        _night.CheckedChanged += (_, _) => { if (!_loading) _app.SetNightMode(_night.Checked); };
        _cap.ValueChanged += (_, _) =>
        {
            if (_loading) return;
            _app.SetVolumeCap((int)_cap.Value);
            if (_volume.Value > _app.Config.VolumeCapPercent) ShowVolume(_app.Config.VolumeCapPercent);
        };
        _hotkeysButton.Click += (_, _) => _app.ShowHotkeys(this);

        ShowSoundOptions();
        ShowHotkeyStatus(_app.UnavailableHotkeys.Count);
    }

    /// <summary>
    /// Switch scene (buttons, tray menu, hotkey). The slider follows at once; the reconnect waits until no
    /// scene change for 1 s, the same rule as the latency slider, so cycling through scenes reconnects once.
    /// </summary>
    public void SelectScene(Scene scene)
    {
        var cfg = _app.Config;
        if (scene == cfg.Scene)
        {
            ShowSoundOptions();
            return;
        }
        if (cfg.Scene == Scene.Custom) // remember the user's own value, even one still inside the slider debounce
            cfg.CustomLatencyMs = _latencyDebounce.Enabled ? _latency.Value * LatencyStep : cfg.LatencyMs;
        cfg.Scene = scene;
        cfg.Save();
        Log.Info($"scene {scene}");

        int ms = Scenes.LatencyMs(scene, Scenes.CustomMs(cfg));
        _latencyDebounce.Stop(); // a pending slider drag is superseded
        _loading = true;
        _latency.Value = Math.Clamp(ms / LatencyStep, _latency.Minimum, _latency.Maximum);
        _loading = false;
        _pendingSceneMs = ms;
        _sceneDebounce.Stop();
        _sceneDebounce.Start();
        ShowSoundOptions();
    }

    /// <summary>Reflect scene, speaker mute, night mode and cap (also after changes from the tray or hotkeys).</summary>
    public void ShowSoundOptions()
    {
        bool loading = _loading;
        _loading = true;
        var cfg = _app.Config;
        foreach (RadioButton b in _scenes.Controls) b.Checked = (Scene)b.Tag! == cfg.Scene;
        _movieHint.Text = L.F("本地播放器：把音频延迟设为 -{0} ms；网页视频：用浏览器插件自动对齐。",
            _app.Controller.SafeLatency(Scenes.MovieMs) + cfg.VideoDelayExtraMs);
        _movieHint.Visible = cfg.Scene == Scene.Movie;
        _speakerMute.Checked = _app.Controller.Muted;
        _night.Checked = cfg.NightMode;
        _cap.Value = Math.Clamp(cfg.VolumeCapPercent, (int)_cap.Minimum, (int)_cap.Maximum);
        _loading = loading;
    }

    /// <summary>Move the volume slider without sending anything (the value was already applied).</summary>
    public void ShowVolume(double percent)
    {
        if (_volume.Capture) return;
        bool loading = _loading;
        _loading = true;
        _volume.Value = Math.Clamp((int)Math.Round(percent), _volume.Minimum, _volume.Maximum);
        _volumeValue.Text = _volume.Value.ToString();
        _loading = loading;
    }

    /// <summary>The button turns red when a hotkey could not be registered (the dialog says which).</summary>
    public void ShowHotkeyStatus(int unavailable)
    {
        _hotkeysButton.ForeColor = unavailable == 0 ? SystemColors.ControlText : Icons.Error;
        _soundTips.SetToolTip(_hotkeysButton, unavailable == 0
            ? L.T("全局快捷键：HomePod 音量、静音、切换场景")
            : L.F("{0} 个快捷键被其他程序占用，点开换一个", unavailable));
    }
}
