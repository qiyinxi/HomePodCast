using HomePodCast.Audio;
using HomePodCast.UI.Controls;

namespace HomePodCast.UI.Pages;

/// <summary>
/// Optional local music separation (音乐分轨), exposed in Mixer. The start switches are session-only. Not in the
/// Microsoft Store version (<see cref="Shown"/>).
/// </summary>
internal sealed class StemPanel : Card
{
    private readonly TrayApp _app;
    private readonly ToggleSwitch _enabled = new(L.T("开启音乐分轨"));
    private readonly ToggleSwitch _monitor = new(L.T("本机播放分轨"));
    private readonly TextBlock _status = Ui.Note();
    private readonly FluentComboBox _device = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private readonly TimerDebounce _saveWait = new(600); // the sliders act at once; the config is written when they rest
    private List<AudioEndpoint> _outputs = [];

    /// <summary>
    /// Whether 混音器 has this panel. Not in the Store version: the feature downloads and starts an executable that is not
    /// part of the package, which the Store's policy 10.2.2 doesn't allow.
    /// </summary>
    internal static bool Shown(bool packaged) => !packaged;

    /// <summary>The guide (docs/MUSIC_STEMS.md) on main, at the section in the user's language.</summary>
    internal static string GuideUrl(string language) =>
        $"{AboutPage.RepositoryUrl}/blob/main/docs/MUSIC_STEMS.md#{(language is "zh-CN" or "zh-TW" ? "简体中文" : "english")}";

    public StemPanel(TrayApp app)
    {
        _app = app;
        var pick = new FluentButton(L.T("选择分轨模块"));
        var help = new FluentButton(L.T("安装说明"), ButtonKind.Link);
        var title = Ui.Header(L.T("音乐分轨（实验性）")); // experimental since 1.1
        Controls.Add(Ui.Row(12, title, title, _enabled));
        Controls.Add(Ui.Note(L.F("在本机分离音乐的人声与伴奏，分别调节 HomePod 和耳机里的声音。额外延迟约 {0} ms；分离可能串音，适合听歌和练唱。", LiveStemMixer.ExtraLatencyMs)));
        Controls.Add(Ui.Row(8, null, pick, help));
        Controls.Add(GainRow(L.T("HomePod 人声"), () => app.Config.Stems.HomePodVocals, v => app.Config.Stems.HomePodVocals = v));
        Controls.Add(GainRow(L.T("HomePod 伴奏"), () => app.Config.Stems.HomePodAccompaniment, v => app.Config.Stems.HomePodAccompaniment = v));
        Controls.Add(Ui.Row(8, null, _monitor));
        _device.AccessibleName = L.T("监听设备");
        Controls.Add(new FieldRow(L.T("监听设备"), _device));
        Controls.Add(GainRow(L.T("本机人声"), () => app.Config.Stems.MonitorVocals, v => app.Config.Stems.MonitorVocals = v));
        Controls.Add(GainRow(L.T("本机伴奏"), () => app.Config.Stems.MonitorAccompaniment, v => app.Config.Stems.MonitorAccompaniment = v));
        Controls.Add(Ui.Note(L.T("监听请选择耳机等未被采集的输出设备。原始声音仍由 Windows 播放；可把音乐程序设为只送 HomePod，或使用独立采集设备。麦克风不会参与分离。")));
        Controls.Add(_status);
        pick.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = L.T("选择包含分轨程序和模型的文件夹") };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            if (!LiveStemMixer.ModuleExists(dialog.SelectedPath))
            {
                MessageBox.Show(this, L.T("文件夹缺少分轨程序或模型，请按安装说明安装完整模块。")); return;
            }
            app.Stems.SetEnabled(false, "for another module folder"); _enabled.Checked = _monitor.Checked = false;
            app.Config.Stems.ModuleDirectory = dialog.SelectedPath; app.Config.Save(); RefreshStatus();
        };
        help.Click += (_, _) => AppEnvironment.OpenUri(GuideUrl(L.Language));
        _enabled.Toggled += (_, _) =>
        {
            try { app.Stems.SetEnabled(_enabled.Checked); }
            catch (FileNotFoundException) { _enabled.Checked = false; MessageBox.Show(this, L.T("请先选择已安装的分轨模块。")); }
            if (!_enabled.Checked) _monitor.Checked = false;
            RefreshStatus();
        };
        _monitor.Toggled += (_, _) => { app.Stems.SetMonitor(_monitor.Checked); RefreshStatus(); };
        _device.SelectionChangeCommitted += (_, _) =>
        {
            int at = _device.SelectedIndex;
            if (at == 0) app.Config.Stems.MonitorDeviceId = null;
            else if (at > 0 && at <= _outputs.Count) app.Config.Stems.MonitorDeviceId = _outputs[at - 1].Id;
            else return;
            app.Config.Save(); if (_monitor.Checked) app.Stems.SetMonitor(true);
        };
        _saveWait.Tick += SaveNow;
        _timer.Tick += (_, _) => RefreshStatus();
        RefreshStatus();
    }

    private FieldRow GainRow(string caption, Func<int> get, Action<int> set)
    {
        var slider = Ui.Slider(0, 100); slider.Value = Math.Clamp(get(), 0, 100);
        slider.AccessibleName = caption;
        var value = new TextBlock(slider.Value.ToString());
        slider.Scroll += (_, _) =>
        {
            set(slider.Value); value.Text = slider.Value.ToString();
            _app.Stems.ApplyGains(); // heard at once, slewed by the mixer (no zipper noise)
            _saveWait.Restart();
        };
        return new FieldRow(caption, slider, value);
    }

    private void SaveNow()
    {
        _saveWait.Stop();
        _app.Config.Save();
    }

    /// <summary>Page shown: the outputs afresh (a headset plugged in since shows up), the switches, the status.</summary>
    public void Start()
    {
        LoadOutputs();
        _enabled.Checked = _app.Stems.Enabled; _monitor.Checked = _app.Stems.MonitorOn;
        RefreshStatus();
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        if (_saveWait.Enabled) SaveNow();
    }

    /// <summary>The monitor devices; again when a device comes or goes (混音器 passes it on).</summary>
    public void LoadOutputs()
    {
        if (_device.DroppedDown) return; // the next device change or page visit refreshes it
        _outputs = AudioEndpoints.Outputs();
        string? chosen = _app.Config.Stems.MonitorDeviceId;
        _device.Items.Clear();
        _device.Items.Add(L.T("Windows 默认输出"));
        foreach (var output in _outputs) _device.Items.Add(output.Name);
        int index = _outputs.FindIndex(o => CaptureEndpoint.Same(o.Id, chosen));
        if (chosen != null && index < 0)
        {
            _device.Items.Add(L.T("所选设备不可用")); _device.SelectedIndex = _device.Items.Count - 1;
        }
        else _device.SelectedIndex = index + 1;
    }

    private void RefreshStatus()
    {
        var stems = _app.Stems;
        _enabled.Checked = stems.Enabled;
        _monitor.Checked = stems.MonitorOn;
        _monitor.Enabled = stems.Enabled;
        bool failed = !stems.Enabled && stems.Error != StemError.None;
        _status.Text = failed ? L.F("分轨已关闭。{0}详细原因见日志。", TrayApp.StemErrorText(stems.Error))
            : !stems.Enabled ? L.T("分轨已关闭；开启后在本机处理，不上传声音。")
            : !stems.Ready ? L.T("正在加载分轨模型…")
            : L.F("分轨运行中：每块处理 {0:F1} ms，未及时完成 {1} 帧，丢弃 {2} 块。", stems.InferenceMs, stems.MissedFrames, stems.DroppedBlocks);
        if (stems.MonitorBlocked) _status.Text += "\n" + L.T(LocalMonitor.CapturedText);
        else if (stems.MonitorError is { } monitorError) _status.Text += "\n" + L.F("本机监听打不开：{0}", monitorError);
        _status.Role = failed || stems.MonitorBlocked ? TextRole.Caution : TextRole.Secondary;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            if (_saveWait.Enabled) SaveNow();
            _saveWait.Dispose();
        }
        base.Dispose(disposing);
    }
}
