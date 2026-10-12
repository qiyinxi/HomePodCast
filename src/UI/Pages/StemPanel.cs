using HomePodCast.Audio;
using HomePodCast.UI.Controls;

namespace HomePodCast.UI.Pages;

/// <summary>Optional local music separation, exposed in Mixer. Start switches are session-only.</summary>
internal sealed class StemPanel : Card
{
    private readonly TrayApp _app;
    private readonly ToggleSwitch _enabled = new(L.T("开启音乐分轨"));
    private readonly ToggleSwitch _monitor = new(L.T("本机播放分轨"));
    private readonly TextBlock _status = Ui.Note();
    private readonly FluentComboBox _device = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private readonly List<AudioEndpoint> _outputs;

    public StemPanel(TrayApp app)
    {
        _app = app;
        var pick = new FluentButton(L.T("选择分轨模块"));
        var help = new FluentButton(L.T("安装说明"), ButtonKind.Link);
        var title = Ui.Header(L.T("音乐分轨（实验性）"));
        Controls.Add(Ui.Row(12, title, title, _enabled));
        Controls.Add(Ui.Note(L.F("在本机分离音乐的人声与伴奏，分别调节 HomePod 和耳机里的声音。额外延迟约 {0} ms；分离可能串音，适合听歌和练唱。", LiveStemMixer.ExtraLatencyMs)));
        Controls.Add(Ui.Row(8, null, pick, help));
        Controls.Add(GainRow(L.T("HomePod 人声"), () => app.Config.Stems.HomePodVocals, v => app.Config.Stems.HomePodVocals = v));
        Controls.Add(GainRow(L.T("HomePod 伴奏"), () => app.Config.Stems.HomePodAccompaniment, v => app.Config.Stems.HomePodAccompaniment = v));
        Controls.Add(Ui.Row(8, null, _monitor));
        _outputs = AudioEndpoints.Outputs();
        _device.Items.Add(L.T("Windows 默认输出"));
        foreach (var output in _outputs) _device.Items.Add(output.Name);
        int index = _outputs.FindIndex(o => CaptureEndpoint.Same(o.Id, app.Config.Stems.MonitorDeviceId));
        if (app.Config.Stems.MonitorDeviceId != null && index < 0)
        {
            _device.Items.Add(L.T("所选设备不可用")); _device.SelectedIndex = _device.Items.Count - 1;
        }
        else _device.SelectedIndex = index + 1;
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
            app.Stems.SetEnabled(false); _enabled.Checked = _monitor.Checked = false;
            app.Config.Stems.ModuleDirectory = dialog.SelectedPath; app.Config.Save(); RefreshStatus();
        };
        help.Click += (_, _) => AppEnvironment.OpenUri("https://github.com/qiyinxi/HomePodCast/blob/dev/docs/MUSIC_STEMS.md");
        _enabled.Toggled += (_, _) =>
        {
            try { app.Stems.SetEnabled(_enabled.Checked); }
            catch (Exception) { _enabled.Checked = false; MessageBox.Show(this, L.T("请先选择已安装的分轨模块。")); }
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
        _timer.Tick += (_, _) => RefreshStatus();
        RefreshStatus();
    }
    private FieldRow GainRow(string caption, Func<int> get, Action<int> set)
    {
        var slider = Ui.Slider(0, 100); slider.Value = Math.Clamp(get(), 0, 100);
        var value = new TextBlock(slider.Value.ToString());
        slider.Scroll += (_, _) => { set(slider.Value); value.Text = slider.Value.ToString(); _app.Stems.ApplyGains(); _app.Config.Save(); };
        return new FieldRow(caption, slider, value);
    }
    public void Start() { _enabled.Checked = _app.Stems.Enabled; _monitor.Checked = _app.Stems.MonitorOn; RefreshStatus(); _timer.Start(); }
    public void Stop() => _timer.Stop();
    private void RefreshStatus()
    {
        var stems = _app.Stems;
        _enabled.Checked = stems.Enabled;
        _monitor.Checked = stems.MonitorOn;
        _monitor.Enabled = stems.Enabled;
        _status.Text = !stems.Enabled ? L.T("分轨已关闭；开启后在本机处理，不上传声音。")
            : stems.Error != null ? L.F("分轨模块失败：{0}。请关闭后重试。", stems.Error)
            : !stems.Ready ? L.T("正在加载分轨模型…")
            : L.F("分轨运行中：每块处理 {0:F1} ms，未及时完成 {1} 帧，丢弃 {2} 块。", stems.InferenceMs, stems.MissedFrames, stems.DroppedBlocks);
        if (stems.MonitorBlocked) _status.Text += "\n" + L.T(LocalMonitor.CapturedText);
        else if (stems.MonitorError != null) _status.Text += "\n" + stems.MonitorError;
    }
    protected override void Dispose(bool disposing) { if (disposing) _timer.Dispose(); base.Dispose(disposing); }
}
