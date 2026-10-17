using HomePodCast.Audio;
using HomePodCast.Net;
using HomePodCast.UI.Controls;

namespace HomePodCast.UI;

/// <summary>
/// 无线影院 (experimental since 1.1, never tested on real hardware: the maintainer has one HomePod, so the label stays
/// until it has been): a local file's channels to one HomePod each, and an explicitly started acoustic alignment.
/// Nothing starts when the window opens; closing it stops what it started, and only that.
/// </summary>
internal class CinemaPage : ScrollPage
{
    private readonly ICinemaHost _host;
    private readonly TextBlock _file = Ui.Note();
    private readonly TextBlock _status = new("", TextStyle.Body, TextRole.Secondary, wrap: true);
    private readonly TextBlock _calibrationStatus = Ui.Note();
    private readonly FluentButton _choose = new(L.T("选择影音文件"));
    private readonly FluentButton _play = new(L.T("播放"), ButtonKind.Primary), _pause = new(L.T("暂停")), _stop = new(L.T("停止"));
    private readonly FluentButton _calibrate = new(L.T("测量音箱延迟"));
    private readonly FluentButton _manualAlignment = new(L.T("手动校准"));
    private readonly FluentButton _saveGroup = new(L.T("保存分组"));
    private readonly FluentComboBox _microphone = new() { MinWidth = 160 };
    private readonly FluentSlider _volume = Ui.Slider(0, 100, 1, 5);
    private readonly TextBlock _volumeValue = new("");
    private readonly IDebounce _volumeWait;
    private readonly IReadOnlyList<AudioEndpoint> _inputs;
    private GroupPanel _editor;
    private string _layoutSignature;
    private readonly FluentComboBox _sourceKind = new() { AccessibleName = L.T("音源"), MinWidth = 180 };
    private readonly FluentButton _apply = new(L.T("应用"), ButtonKind.Primary);
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private CinemaFileSource? _source;
    private CinemaSpeaker[] _activeLayout = [];
    private CancellationTokenSource? _measurement;
    private string? _path;
    private long _endedAt;
    private bool _fileMultichannel;
    private uint _fileMask;
    private long _playRequest;
    private long? _openingRequest;
    private CinemaMediaReader.Progress? _inspection;
    private CinemaFileSource? _openingSource;
    internal Func<CinemaFileSource, Task<bool>> WaitForFileOpened { get; set; } = WaitOpenedAsync;

    /// <summary>The decoder's own signal, awaited for up to 10 s: no thread is held while it opens.</summary>
    private static async Task<bool> WaitOpenedAsync(CinemaFileSource source)
    {
        try { return await source.Opened.WaitAsync(TimeSpan.FromSeconds(10)) && source.Error == null; }
        catch (TimeoutException) { return false; }
    }

    /// <summary>What a file being opened waits for, e.g. for a test's timeout message.</summary>
    internal string OpeningState => _openingRequest == null ? "nothing is opening"
        : _openingSource is { } source ? "the decoder is " + source.Progress
        : _inspection is { } inspection ? "the file inspection is " + inspection
        : "opening";

    /// <param name="inputs">The microphones to offer (null: the system's capture endpoints).</param>
    /// <param name="volumeWait">The volume slider's save debounce (tests pass a manual one).</param>
    internal CinemaPage(ICinemaHost host, IReadOnlyList<AudioEndpoint>? inputs = null, IDebounce? volumeWait = null)
        : base(L.T("多音箱（实验性）")) // experimental since 1.1; hardware untested
    {
        _host = host;
        TitleText!.Wrap = true;
        var config = host.Config;
        config.Cinema ??= new CinemaSettings();
        var body = Content;
        body.Controls.Add(new TextBlock(L.T("实验性功能：无线影院还没有在多台真实的 HomePod 上验证过，声道、同步和测量结果都可能不对。"),
            TextStyle.BodyStrong, TextRole.Caution, wrap: true)); // experimental since 1.1; hardware untested
        _sourceKind.Items.Add(L.T("电脑实时声音"));
        _sourceKind.Items.Add(L.T("本地影音文件"));
        _sourceKind.SelectedIndex = 0;
        body.Controls.Add(new FieldRow(L.T("音源"), _sourceKind));
        body.Controls.Add(Ui.Row(8, null, _choose, _play, _pause, _stop));
        body.Controls.Add(_file);
        body.Controls.Add(Ui.Note(L.T("本地文件只播放音轨，不显示视频。切换音源或离开此页会停止本页的文件播放。")));
        body.Controls.Add(Ui.Note(L.T("在电脑实时声音下应用设置，才会更新实时推送的音箱；播放文件不会改变实时推送的音箱组。")));
        body.Controls.Add(Ui.Section(L.T("音箱位置")));
        _editor = new GroupPanel(config);
        _editor.SetFound(host.Speakers.ToList());
        _layoutSignature = GroupPanel.SettingsSignature(config);
        body.Controls.Add(_editor);
        var groupActions = Ui.Row(8, null, _apply, _saveGroup, _manualAlignment);
        groupActions.Wrap = true;
        body.Controls.Add(groupActions);
        _saveGroup.Click += (_, _) => SaveOutputGroup();
        _manualAlignment.Click += (_, _) => AlignManually();
        _apply.Click += (_, _) =>
        {
            if (_editor.BlockReason is { } blocked) { _status.Text = blocked; return; }
            var before = GroupForm.PlayLayout(config);
            _editor.Apply(activateCapture: _sourceKind.SelectedIndex == 0);
            _layoutSignature = GroupPanel.SettingsSignature(config);
            if (host is TrayApp app) GroupForm.Save(app, before);
            else config.Save();
        };
        var fieldRows = new List<FieldRow>();
        _sourceKind.SelectedIndexChanged += (_, _) =>
        {
            StopPlayback("source changed");
            _editor.SetSource(_sourceKind.SelectedIndex == 1 && _fileMultichannel, fileSource: _sourceKind.SelectedIndex == 1, mask: _fileMask);
            RefreshStatus();
        };

        var alignment = Ui.Section(L.T("延迟测量"));
        body.Controls.Add(alignment);
        body.GapBefore[alignment] = 16;
        _inputs = inputs ?? AudioEndpoints.Inputs();
        _microphone.AccessibleName = L.T("校准麦克风");
        _microphone.Items.Add(L.T("系统默认"));
        foreach (var input in _inputs) _microphone.Items.Add(input.Name);
        int mic = _inputs.ToList().FindIndex(i => i.Id == config.Cinema.CalibrationMicId);
        if (config.Cinema.CalibrationMicId != null && mic < 0)
        {
            _microphone.Items.Add(L.T("未发现"));
            _microphone.SelectedIndex = _microphone.Items.Count - 1;
        }
        else _microphone.SelectedIndex = mic + 1;
        var micRow = new FieldRow(L.T("校准麦克风"), Ui.Row(8, _microphone, _microphone, _calibrate));
        fieldRows.Add(micRow);
        body.Controls.Add(micRow);
        body.Controls.Add(Ui.Note(L.T("播放时可以测量：每台音箱依次发出一声短促的测试音，测量期间音箱音量暂时不超过 25%。麦克风只在测量时打开，录音只在内存里分析、不保存也不上传。测量失败时原来的延迟不变。")));
        body.Controls.Add(_calibrationStatus); // kept apart from the playback status, which updates 4 times a second

        int cap = (int)Math.Round(host.VolumeCap);
        _volume.Ceiling = cap;
        _volume.Mark = cap < 100 ? cap : null;
        _volume.AccessibleName = L.T("音量");
        ShowVolume(host.Volume ?? 20);
        var volumeRow = new FieldRow(L.T("音量"), _volume, _volumeValue);
        fieldRows.Add(volumeRow);
        body.Controls.Add(volumeRow);
        body.GapBefore[volumeRow] = 16;
        body.Controls.Add(_status);
        FieldRow.AlignCaptions([.. fieldRows]);

        _volumeWait = volumeWait ?? new TimerDebounce(600);
        _volume.Scroll += (_, _) =>
        {
            _volumeValue.Text = _volume.Value.ToString();
            _host.PreviewVolume(_volume.Value); // the speakers follow the drag; the debounce saves it like 首页's volume
            _volumeWait.Restart();
        };
        _volumeWait.Tick += FlushVolume;
        _choose.Click += async (_, _) => await ChooseFileAsync();
        _play.Click += async (_, _) => await PlayAsync();
        _pause.Click += (_, _) => TogglePause();
        _stop.Click += (_, _) => { StopPlayback("stop"); _status.Text = ""; RefreshStatus(); };
        _calibrate.Click += async (_, _) => await CalibrateAsync();
        _timer.Tick += (_, _) => RefreshStatus();
        RefreshStatus();
    }

    public override void PageShown()
    {
        RefreshSpeakers();
        _timer.Start();
        RefreshStatus();
    }

    internal void RefreshSpeakers()
    {
        if (_source != null || _measurement != null || _openingRequest != null) return;
        if (_layoutSignature != GroupPanel.SettingsSignature(_host.Config))
        {
            int index = Content.Controls.GetChildIndex(_editor);
            _editor.Dispose();
            _editor = new GroupPanel(_host.Config);
            Content.Controls.Add(_editor); Content.Controls.SetChildIndex(_editor, index);
            _editor.SetSource(_sourceKind.SelectedIndex == 1 && _fileMultichannel, fileSource: _sourceKind.SelectedIndex == 1, mask: _fileMask);
        }
        _editor.SetFound(_host.Speakers.ToList());
        _layoutSignature = GroupPanel.SettingsSignature(_host.Config);
    }

    public override void PageHidden()
    {
        _timer.Stop();
        FlushVolume();
        StopPlayback("page hidden");
        _status.Text = "";
        RefreshStatus();
    }

    // ---------------------------------------------------------------- playback

    private async Task ChooseFileAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = L.T("影音文件") + "|*.wav;*.mp4;*.m4a;*.m4v;*.mkv;*.mov;*.wma;*.aac;*.ac3;*.ec3|" + L.T("所有文件") + "|*.*",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) await UseFile(dialog.FileName);
    }

    /// <summary>The file to play (the file dialog, or a test).</summary>
    internal async Task UseFile(string path)
    {
        StopPlayback("another file");
        _sourceKind.SelectedIndex = 1;
        long request = _playRequest;
        _openingRequest = request;
        _path = null;
        _file.Text = Path.GetFileName(path);
        _status.Text = "";
        RefreshStatus();
        var inspection = _inspection = new CinemaMediaReader.Progress("starting the inspection thread");
        try
        {
            // Media Foundation requires an MTA thread; the WinForms UI is STA. Keep native open off the UI thread too.
            var info = await CinemaMediaReader.InspectAsync(path, step => inspection.Step("opening the file: " + step));
            if (IsDisposed || request != _playRequest) return;
            _path = path;
            _fileMultichannel = info.Channels >= 6;
            _fileMask = info.Mask;
            _file.Text += " · " + CinemaChannels.DisplayName(info.Channels, info.Mask);
            _editor.SetSource(_fileMultichannel, fileSource: true, mask: _fileMask);
        }
        catch (CinemaException ex)
        {
            if (IsDisposed || request != _playRequest) return;
            _path = path; _status.Text = ex.Message; _fileMultichannel = false; _fileMask = 0;
            _editor.SetSource(false, fileSource: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"cinema: file inspection failed: {ex.GetType().Name}: {ex.Message}");
            if (IsDisposed || request != _playRequest) return;
            _path = path; _status.Text = L.T("出现了意外的错误，详情见日志。"); _fileMultichannel = false; _fileMask = 0;
            _editor.SetSource(false, fileSource: true);
        }
        finally
        {
            if (_openingRequest == request) _openingRequest = null;
            if (ReferenceEquals(_inspection, inspection)) _inspection = null;
            if (!IsDisposed) RefreshStatus();
        }
    }

    private CinemaSpeaker[] SelectedLayout() => _editor.SelectedLayout();

    internal async Task PlayAsync()
    {
        if (_path == null || _measurement != null || _openingRequest != null) return;
        if (_editor.BlockReason is { } blocked) { _status.Text = blocked; return; }
        StopPlayback("restart");
        long request = _playRequest;
        _openingRequest = request;
        RefreshStatus();
        _calibrationStatus.Text = "";
        FlushVolume(); // the slider's value is what the cinema starts with, and it is saved first
        CinemaFileSource? source = null;
        _play.Enabled = false;
        try
        {
            var layout = SelectedLayout();
            CinemaMatrix.Validate(layout);
            source = _openingSource = new CinemaFileSource(_path, layout);
            _status.Text = L.T("正在打开文件…");
            bool opened = await WaitForFileOpened(source);
            if (IsDisposed || request != _playRequest) { source.Dispose(); return; }
            if (!opened)
                throw source.Error is { } error ? new CinemaException(error, source.ErrorLog ?? error)
                    : new CinemaException(L.T("打开文件太久没有反应，已放弃。"), $"the decoder did not open the file within 10 s: {source.Progress}");
            _editor.Apply(activateCapture: false);
            _layoutSignature = GroupPanel.SettingsSignature(_host.Config);
            _host.Config.Cinema.Speakers = layout.ToList();
            _host.Config.Save();
            _activeLayout = layout; _source = source; _endedAt = 0;
            _host.StartCinema(source, layout, _volume.Value);
        }
        catch (CinemaException ex)
        {
            source?.Dispose();
            Log.Warn($"cinema: not started: {ex.LogText}");
            if (!IsDisposed && request == _playRequest) _status.Text = L.F("影院播放失败：{0}", ex.Message);
        }
        catch (Exception ex)
        {
            source?.Dispose();
            Log.Warn($"cinema: not started: {ex.GetType().Name}: {ex.Message}");
            if (!IsDisposed && request == _playRequest) _status.Text = L.F("影院播放失败：{0}", L.T("出现了意外的错误，详情见日志。"));
        }
        finally
        {
            if (_openingRequest == request) _openingRequest = null;
            if (ReferenceEquals(_openingSource, source)) _openingSource = null;
            if (!IsDisposed)
            {
                _play.Enabled = true;
                RefreshStatus();
            }
        }
    }

    private void TogglePause()
    {
        if (_source is not { } source) return;
        source.Paused = !source.Paused;
        Log.Info($"cinema: {(source.Paused ? "paused" : "resumed")} at {source.PositionSeconds:F1} s");
        RefreshStatus();
    }

    private void StopPlayback(string reason)
    {
        _playRequest++; // a file still opening must not connect after a source change or after the page is hidden
        _openingRequest = null;
        _measurement?.Cancel();
        var source = _source;
        _source = null;
        if (source != null) _host.StopCinema(source, reason);
    }

    /// <summary>Every 250 ms: what the cinema does now, and whether it ended by itself or was replaced.</summary>
    internal void RefreshStatus()
    {
        var source = _source;
        var state = source == null ? CinemaState.Ended : _host.CinemaStateOf(source);
        if (source != null && _measurement == null)
        {
            if (source.Error != null)
            {
                Log.Warn($"cinema: {source.ErrorLog}");
                StopPlayback("decode error");
                _status.Text = L.F("影院播放失败：{0}", source.Error);
            }
            else if (state == CinemaState.Ended)
            {
                // Another connection (连接 on 首页 or the tray, a profile, a hotkey) or another device took the speakers.
                StopPlayback("replaced");
                _status.Text = L.T("无线影院已停止：音箱改由其他连接或设备使用了。");
            }
            else if (source.Finished && state == CinemaState.Playing)
            {
                if (_endedAt == 0) _endedAt = Environment.TickCount64 + _host.LatencyMs + 150; // let the speakers play out
                if (Environment.TickCount64 >= _endedAt)
                {
                    StopPlayback("finished");
                    _status.Text = L.T("播放完毕。");
                }
            }
        }
        source = _source;
        state = source == null ? CinemaState.Ended : _host.CinemaStateOf(source);

        bool active = source != null;
        bool opening = _openingRequest != null;
        _play.Enabled = !opening && _sourceKind.SelectedIndex == 1 && _path != null && _measurement == null && _editor.BlockReason == null;
        _choose.Enabled = _sourceKind.SelectedIndex == 1 && _measurement == null;
        _apply.Enabled = !active && !opening && _editor.BlockReason == null;
        _saveGroup.Enabled = !active && !opening && _sourceKind.SelectedIndex == 0 && _editor.LiveOutputCount > 0;
        _manualAlignment.Enabled = !active && !opening && _sourceKind.SelectedIndex == 0 && _editor.LiveOutputCount >= 2;
        _pause.Enabled = _stop.Enabled = active && _measurement == null;
        _pause.Text = source?.Paused == true ? L.T("继续") : L.T("暂停");
        _calibrate.Enabled = state == CinemaState.Playing && _measurement == null && _activeLayout.Length >= 2;
        _microphone.Enabled = !active && !opening;
        _editor.SetPlaybackActive(active || opening);
        if (_host is TrayApp app)
            _editor.ShowOutputStatus(app.Controller.GroupOutputStatuses,
                app.Controller.State == StreamState.Streaming && app.Controller.GroupOutputStatuses.Count > 0 ? app.Controller.EffectiveLatencyMs : null);
        if (source != null)
        {
            string layout = CinemaChannels.DisplayName(source.Channels, source.Mask);
            _status.Text = state switch
            {
                CinemaState.Playing when source.Paused => L.F("已暂停：{0} · {1:F1} 秒", layout, source.PositionSeconds),
                CinemaState.Playing => L.F("正在播放：{0} · {1:F1} 秒", layout, source.PositionSeconds),
                _ => _host.StatusText, // connecting, waiting for a busy speaker, retrying
            };
        }
        ShowSoundOptions();
    }

    private void SaveOutputGroup()
    {
        if (!_saveGroup.Enabled) return;
        using var dialog = new OutputGroupNameDialog();
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        var existing = _host.Config.SavedOutputGroups.FirstOrDefault(g => string.Equals(g.Name, dialog.GroupName, StringComparison.OrdinalIgnoreCase));
        if (existing != null && MessageBox.Show(this, L.T("用编辑器中的设置覆盖同名分组吗？"), L.T("保存分组"),
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        try
        {
            OutputGroups.Save(_host.Config, dialog.GroupName, _editor.CaptureSettings(), existing?.Id);
            _host.Config.Save();
            _status.Text = L.T("分组已保存，可在首页选择后连接。");
        }
        catch (Exception ex)
        {
            Log.Warn($"output group save: {ex.Message}");
            _status.Text = L.T("保存失败，请稍后重试。");
        }
    }

    private void AlignManually()
    {
        if (!_manualAlignment.Enabled) return;
        bool test = _host is TrayApp app && app.CanTestOutputGroup && _editor.HasAppliedSelection();
        using var dialog = new GroupAlignmentForm(_editor.AlignmentOutputs(), test,
            _host is TrayApp live ? live.RunGroupAlignmentTest : null);
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK && dialog.Adjustment is { } adjustment)
        {
            _editor.ApplyManualTrim(adjustment);
            _status.Text = L.T("微调已写入编辑器，请点应用更新分组。");
        }
    }

    // ---------------------------------------------------------------- volume

    /// <summary>The page outlives settings edits; its limit must follow the current global cap.</summary>
    internal void ShowSoundOptions()
    {
        int cap = (int)Math.Round(_host.VolumeCap);
        _volume.Ceiling = cap;
        _volume.Mark = cap < 100 ? cap : null;
        if (!_volume.IsDragging && !_volumeWait.Enabled && _host.Volume is { } volume) ShowVolume(volume);
    }

    internal void Flush() => FlushVolume();

    private void ShowVolume(double percent)
    {
        _volume.Value = (int)Math.Round(Math.Min(percent, _volume.Ceiling));
        _volumeValue.Text = _volume.Value.ToString();
    }

    /// <summary>The slider's debounce (or Play, or closing): saved and sent like 首页's volume.</summary>
    private void FlushVolume()
    {
        if (!_volumeWait.Enabled) return;
        _volumeWait.Stop();
        _host.ApplyVolume(_volume.Value);
    }

    // ---------------------------------------------------------------- acoustic alignment

    private async Task CalibrateAsync()
    {
        if (_source is not { } source || _host.CinemaStateOf(source) != CinemaState.Playing || _measurement != null) return;
        using var cancellation = new CancellationTokenSource();
        _measurement = cancellation;
        var layout = _activeLayout;
        RefreshStatus();
        try
        {
            int choice = _microphone.SelectedIndex;
            string? mic = choice <= 0 ? null : choice <= _inputs.Count ? _inputs[choice - 1].Id : _host.Config.Cinema.CalibrationMicId;
            _calibrationStatus.Text = L.T("正在测量音箱延迟…");
            Log.Info($"cinema calibration: start, {layout.Length} speakers, {(mic == null ? "default microphone" : "chosen microphone")}");
            AcousticMeasurement[] readings;
            using (_host.LimitVolumeForMeasurement())
                readings = await CinemaAcousticCalibrator.MeasureAsync(source, layout.Select(s => s.Name).ToList(), mic,
                    () => ReferenceEquals(_source, source) && _host.CinemaStateOf(source) == CinemaState.Playing, cancellation.Token);
            if (IsDisposed || !ReferenceEquals(_source, source)) return;
            var delays = AcousticAlignment.RelativeOffsets(readings);
            for (int i = 0; i < delays.Length; i++)
            {
                layout[i].DelayMs = delays[i];
            }
            source.ApplyDelays(delays);
            _editor.ApplyDelays(layout);
            _host.Config.Cinema.CalibrationMicId = mic;
            _host.Config.Cinema.Speakers = layout.ToList();
            _host.Config.Save();
            Log.Info("cinema calibration: " + string.Join(", ", layout.Select((s, i) =>
                $"{s.Role} arrival {readings[i].DelayMs:F1} ms (match {readings[i].Confidence:F2}) → +{delays[i]} ms")));
            _calibrationStatus.Text = L.T("音箱延迟校准已应用。");
        }
        catch (OperationCanceledException)
        {
            Log.Info("cinema calibration: cancelled");
            if (!IsDisposed) _calibrationStatus.Text = "";
        }
        catch (CinemaException ex)
        {
            Log.Warn($"cinema calibration failed: {ex.LogText}");
            if (!IsDisposed) _calibrationStatus.Text = L.F("音箱校准失败：{0}", ex.Message);
        }
        catch (Exception ex)
        {
            Log.Warn($"cinema calibration failed: {ex.GetType().Name}: {ex.Message}");
            if (!IsDisposed) _calibrationStatus.Text = L.F("音箱校准失败：{0}", L.T("出现了意外的错误，详情见日志。"));
        }
        finally
        {
            _measurement = null;
            if (!IsDisposed) RefreshStatus();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            FlushVolume();
            StopPlayback("window closed");
            _timer.Dispose();
            (_volumeWait as IDisposable)?.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class CinemaForm : Form, ISurface, ILayoutRoot
{
    private readonly CinemaPage _page;
    internal CinemaForm(ICinemaHost host, IReadOnlyList<AudioEndpoint>? inputs = null, IDebounce? volumeWait = null)
    {
        AutoScaleMode = AutoScaleMode.None;
        Text = L.T("无线影院（实验性）"); // experimental since 1.1; hardware untested
        Icon = Icons.App; StartPosition = FormStartPosition.CenterParent;
        _page = new CinemaPage(host, inputs, volumeWait);
        _page.Dock = DockStyle.Fill;
        Controls.Add(_page);
        ClientSize = new Size(Theme.Dp(680, DeviceDpi), Theme.Dp(640, DeviceDpi));
    }
    public Color SurfaceColor => Theme.P.Window;
    internal Task UseFile(string path) => _page.UseFile(path);
    internal Task PlayAsync() => _page.PlayAsync();
    internal void RefreshStatus() => _page.RefreshStatus();
    internal string OpeningState => _page.OpeningState;
    protected override void OnShown(EventArgs e) { base.OnShown(e); _page.PageShown(); }
    protected override void OnFormClosed(FormClosedEventArgs e) { _page.PageHidden(); base.OnFormClosed(e); }
    protected override void OnDpiChanged(DpiChangedEventArgs e) => LayoutPanel.ScaleForDpi(this, () => base.OnDpiChanged(e));
    public static void ShowFor(TrayApp app, IWin32Window? owner)
    {
        using var form = new CinemaForm(app);
        form.ShowDialog(owner);
    }
}
