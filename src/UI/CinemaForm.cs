using HomePodCast.Audio;
using HomePodCast.Net;
using HomePodCast.UI.Controls;

namespace HomePodCast.UI;

/// <summary>
/// 无线影院 (experimental since 1.1, never tested on real hardware: the maintainer has one HomePod, so the label stays
/// until it has been): a local file's channels to one HomePod each, and an explicitly started acoustic alignment.
/// Nothing starts when the window opens; closing it stops what it started, and only that.
/// </summary>
internal sealed class CinemaForm : Form, ISurface, ILayoutRoot
{
    private const int ContentWidth = 680; // 96-DPI units

    private readonly ICinemaHost _host;
    private readonly ScrollPage _page;
    private readonly RowPanel _footer;
    private readonly TextBlock _file = Ui.Note();
    private readonly TextBlock _status = new("", TextStyle.Body, TextRole.Secondary, wrap: true);
    private readonly TextBlock _calibrationStatus = Ui.Note();
    private readonly FluentButton _choose = new(L.T("选择影音文件"));
    private readonly FluentButton _play = new(L.T("播放"), ButtonKind.Primary), _pause = new(L.T("暂停")), _stop = new(L.T("停止"));
    private readonly FluentButton _calibrate = new(L.T("测量音箱延迟"));
    private readonly FluentButton _close = new(L.T("关闭")) { DialogResult = DialogResult.Cancel, MinWidth = 96 };
    private readonly FluentComboBox _microphone = new() { MinWidth = 160 };
    private readonly FluentSlider _volume = Ui.Slider(0, 100, 1, 5);
    private readonly TextBlock _volumeValue = new("");
    private readonly IDebounce _volumeWait;
    private readonly IReadOnlyList<AudioEndpoint> _inputs;
    private readonly List<AirPlayDevice> _devices;
    private readonly List<(CinemaRole Role, FluentComboBox Choice, FluentSlider Delay, TextBlock Value)> _rows = [];
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private CinemaFileSource? _source;
    private CinemaSpeaker[] _activeLayout = [];
    private CancellationTokenSource? _measurement;
    private string? _path;
    private long _endedAt;

    public static void ShowFor(TrayApp app, IWin32Window? owner)
    {
        using var form = new CinemaForm(app);
        form.ShowDialog(owner);
    }

    /// <param name="inputs">The microphones to offer (null: the system's capture endpoints).</param>
    /// <param name="volumeWait">The volume slider's save debounce (tests pass a manual one).</param>
    internal CinemaForm(ICinemaHost host, IReadOnlyList<AudioEndpoint>? inputs = null, IDebounce? volumeWait = null)
    {
        _host = host;
        var config = host.Config;
        config.Cinema ??= new CinemaSettings();
        Theme.Watch();
        AutoScaleMode = AutoScaleMode.None;
        // experimental since 1.1; never tested on real hardware, so the label stays until it has been
        Text = L.T("无线影院（实验性）");
        Icon = Icons.App; StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false; MinimizeBox = false;
        BackColor = Theme.P.Window;
        Font = Theme.Font(TextStyle.Body, DeviceDpi);

        _page = new ScrollPage(Text);
        var body = _page.Content;
        body.Inset = new Padding(24, 16, 20, 16);
        body.Controls.Add(new TextBlock(L.T("实验性功能：无线影院还没有在多台真实的 HomePod 上验证过，声道、同步和测量结果都可能不对。"),
            TextStyle.BodyStrong, TextRole.Caution, wrap: true));
        body.Controls.Add(Ui.Note(L.T("把本地影音文件的每个声道送到一台 HomePod：5.1 和 7.1 的环绕声道、中置和低音声道各自独立播放；没有分配音箱的声道混入相邻的音箱，立体声和单声道文件只用前面的音箱。这里只播放音轨，不显示视频。")));

        body.Controls.Add(Ui.Row(8, null, _choose, _play, _pause, _stop));
        body.Controls.Add(_file);

        _devices = host.Speakers.Where(d => d.Model.StartsWith("AudioAccessory", StringComparison.Ordinal) && !StereoPairs.IsPairId(d.DeviceId))
            .GroupBy(d => GroupPlan.Key(d.DeviceId)).Select(g => g.First()).ToList();
        foreach (var saved in config.Cinema.Speakers ?? [])
            if (_devices.All(d => GroupPlan.Key(d.DeviceId) != GroupPlan.Key(saved.DeviceId)))
                _devices.Add(new AirPlayDevice(saved.Name, saved.DeviceId,
                    System.Net.IPAddress.TryParse(saved.Host, out var address) ? address : System.Net.IPAddress.None, 7000, "",
                    new Dictionary<string, string>()));

        var speakers = Ui.Section(L.T("音箱位置"));
        body.Controls.Add(speakers);
        body.GapBefore[speakers] = 16;
        body.Controls.Add(Ui.Note(L.T("至少选择前左和前右。低音声道可以交给一台单独的 HomePod，否则混入前左和前右。滑块是这台音箱额外等待的毫秒数，用来让声音同时到达座位。")));
        var fieldRows = new List<FieldRow>();
        foreach (var role in Enum.GetValues<CinemaRole>())
        {
            var combo = new FluentComboBox { MinWidth = 200, AccessibleName = RoleName(role) };
            combo.Items.Add(L.T("（不使用）"));
            foreach (var device in _devices) combo.Items.Add(device.Name);
            var saved = config.Cinema.Speakers?.FirstOrDefault(s => s.Role == role);
            combo.SelectedIndex = saved == null ? 0 : _devices.FindIndex(d => GroupPlan.Key(d.DeviceId) == GroupPlan.Key(saved.DeviceId)) + 1;
            var delay = Ui.Slider(0, 500, 1, 10);
            delay.AccessibleName = L.F("{0}延迟", RoleName(role));
            delay.Value = Math.Clamp(saved?.DelayMs ?? 0, 0, 500);
            var value = new TextBlock(Milliseconds(delay.Value)) { Align = HorizontalAlignment.Right };
            delay.Scroll += (_, _) => value.Text = Milliseconds(delay.Value);
            var field = Ui.Row(8, delay, combo, delay, value);
            field.FixedWidths[value] = 60;
            var row = new FieldRow(RoleName(role), field);
            fieldRows.Add(row);
            body.Controls.Add(row);
            body.GapBefore[row] = 4;
            _rows.Add((role, combo, delay, value));
        }

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

        var spacer = new TextBlock();
        _footer = Ui.Row(8, spacer, spacer, _close); // 关闭 at the bottom right, outside the scrolling page
        Controls.Add(_page);
        Controls.Add(_footer);
        CancelButton = _close;

        _volumeWait = volumeWait ?? new TimerDebounce(600);
        _volume.Scroll += (_, _) =>
        {
            _volumeValue.Text = _volume.Value.ToString();
            _host.PreviewVolume(_volume.Value); // the speakers follow the drag; the debounce saves it like 首页's volume
            _volumeWait.Restart();
        };
        _volumeWait.Tick += FlushVolume;
        _choose.Click += (_, _) => ChooseFile();
        _play.Click += async (_, _) => await PlayAsync();
        _pause.Click += (_, _) => TogglePause();
        _stop.Click += (_, _) => { StopPlayback("stop"); _status.Text = ""; RefreshStatus(); };
        _calibrate.Click += async (_, _) => await CalibrateAsync();
        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();
        Theme.Changed += ThemeChanged;
        RefreshStatus();
    }

    public Color SurfaceColor => Theme.P.Window;

    private int Dp(float value) => Theme.Dp(value, DeviceDpi);

    internal static string RoleName(CinemaRole role) => role switch
    {
        CinemaRole.FrontLeft => L.T("前左"), CinemaRole.FrontRight => L.T("前右"), CinemaRole.Center => L.T("中置"),
        CinemaRole.Subwoofer => L.T("低音声道"), CinemaRole.RearLeft => L.T("后左"), CinemaRole.RearRight => L.T("后右"),
        CinemaRole.SideLeft => L.T("侧左"), _ => L.T("侧右"),
    };

    private static string Milliseconds(int ms) => L.F("{0} 毫秒", ms);

    // ---------------------------------------------------------------- layout

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // As tall as the content, but never taller than the screen (8 speaker rows at 150 % are not): the page scrolls then.
        int width = Dp(ContentWidth);
        int content = _page.Content.GetPreferredSize(new Size(width - SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi), 0)).Height;
        var area = Screen.FromControl(Owner ?? (Control)this).WorkingArea;
        int chrome = Height - ClientSize.Height;
        int height = Math.Min(content + FooterHeight(width), area.Height - chrome - Dp(16));
        ClientSize = new Size(width, height);
        MinimumSize = new Size(Dp(560) + Width - ClientSize.Width, Math.Min(Dp(360), height) + chrome);
        if (Owner != null) CenterToParent();
    }

    private int FooterHeight(int width) => _footer.GetPreferredSize(new Size(Math.Max(0, width - Dp(48)), 0)).Height + Dp(28);

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_footer is null || _page is null) return; // Form setters lay out before the constructor is done
        int w = ClientSize.Width, h = ClientSize.Height, footer = FooterHeight(w);
        _page.SetBounds(0, 0, w, Math.Max(0, h - footer));
        LayoutPanel.Place(_footer, Dp(24), Math.Max(0, h - footer) + Dp(14), Math.Max(0, w - Dp(48)), footer - Dp(28));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.StyleWindow(this);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Font = Theme.Font(TextStyle.Body, e.DeviceDpiNew);
        PerformLayout();
    }

    private void ThemeChanged()
    {
        if (IsDisposed) return;
        BackColor = Theme.P.Window;
        Theme.StyleWindow(this);
        Invalidate(true);
    }

    // ---------------------------------------------------------------- playback

    private void ChooseFile()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = L.T("影音文件") + "|*.wav;*.mp4;*.m4a;*.m4v;*.mkv;*.mov;*.wma;*.aac;*.ac3;*.ec3|" + L.T("所有文件") + "|*.*",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) UseFile(dialog.FileName);
    }

    /// <summary>The file to play (the file dialog, or a test).</summary>
    internal void UseFile(string path)
    {
        StopPlayback("another file");
        _path = path;
        _file.Text = Path.GetFileName(path);
        _status.Text = "";
        RefreshStatus();
    }

    private CinemaSpeaker[] SelectedLayout() => _rows.Where(r => r.Choice.SelectedIndex > 0).Select(r =>
    {
        var device = _devices[r.Choice.SelectedIndex - 1];
        return new CinemaSpeaker
        {
            Role = r.Role, DeviceId = device.DeviceId, Name = device.Name,
            Host = device.Address.Equals(System.Net.IPAddress.None) ? null : device.Address.ToString(), DelayMs = r.Delay.Value,
        };
    }).ToArray();

    internal async Task PlayAsync()
    {
        if (_path == null || _measurement != null) return;
        StopPlayback("restart");
        _calibrationStatus.Text = "";
        FlushVolume(); // the slider's value is what the cinema starts with, and it is saved first
        CinemaFileSource? source = null;
        _play.Enabled = false;
        try
        {
            var layout = SelectedLayout();
            CinemaMatrix.Validate(layout);
            source = new CinemaFileSource(_path, layout);
            _status.Text = L.T("正在打开文件…");
            var opening = source;
            bool opened = await Task.Run(() => opening.WaitOpened(TimeSpan.FromSeconds(10)));
            if (IsDisposed) { source.Dispose(); return; }
            if (!opened)
                throw source.Error is { } error ? new CinemaException(error, source.ErrorLog ?? error)
                    : new CinemaException(L.T("打开文件太久没有反应，已放弃。"), "the decoder did not open the file within 10 s");
            _host.Config.Cinema.Speakers = layout.ToList();
            _host.Config.Save();
            _activeLayout = layout; _source = source; _endedAt = 0;
            _host.StartCinema(source, layout, _volume.Value);
        }
        catch (CinemaException ex)
        {
            source?.Dispose();
            Log.Warn($"cinema: not started: {ex.LogText}");
            if (!IsDisposed) _status.Text = L.F("影院播放失败：{0}", ex.Message);
        }
        catch (Exception ex)
        {
            source?.Dispose();
            Log.Warn($"cinema: not started: {ex.GetType().Name}: {ex.Message}");
            if (!IsDisposed) _status.Text = L.F("影院播放失败：{0}", L.T("出现了意外的错误，详情见日志。"));
        }
        finally
        {
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
        _play.Enabled = _path != null && _measurement == null;
        _pause.Enabled = _stop.Enabled = active && _measurement == null;
        _pause.Text = source?.Paused == true ? L.T("继续") : L.T("暂停");
        _calibrate.Enabled = state == CinemaState.Playing && _measurement == null && _activeLayout.Length >= 2;
        _microphone.Enabled = !active;
        foreach (var row in _rows) { row.Choice.Enabled = !active; row.Delay.Enabled = !active; }
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
        if (!_volume.IsDragging && !_volumeWait.Enabled && _host.Volume is { } volume) ShowVolume(volume);
    }

    // ---------------------------------------------------------------- volume

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
                var row = _rows.First(r => r.Role == layout[i].Role);
                row.Delay.Value = delays[i];
                row.Value.Text = Milliseconds(delays[i]);
            }
            source.ApplyDelays(delays);
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

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        FlushVolume();
        StopPlayback("window closed");
        _timer.Dispose();
        (_volumeWait as IDisposable)?.Dispose();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= ThemeChanged;
        base.Dispose(disposing);
    }
}
