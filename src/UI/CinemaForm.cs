using HomePodCast.Audio;
using HomePodCast.Net;
using HomePodCast.UI.Controls;

namespace HomePodCast.UI;

/// <summary>Local 5.1/7.1 playback and explicitly initiated acoustic alignment. Nothing starts on opening the dialog.</summary>
internal sealed class CinemaForm : Form
{
    private readonly TrayApp _app;
    private readonly TextBlock _file = Ui.Note();
    private readonly TextBlock _status = Ui.Note();
    private readonly TextBlock _calibrationStatus = Ui.Note();
    private readonly FluentButton _play = new(L.T("播放")), _pause = new(L.T("暂停")), _stop = new(L.T("停止"));
    private readonly FluentButton _calibrate = new(L.T("测量音箱延迟"));
    private readonly FluentComboBox _microphone = new();
    private readonly List<AudioEndpoint> _inputs;
    private readonly List<AirPlayDevice> _devices;
    private readonly List<(CinemaRole Role, FluentComboBox Choice, FluentSlider Delay, TextBlock Value)> _rows = [];
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private CinemaFileSource? _source;
    private CinemaSpeaker[] _activeLayout = [];
    private CancellationTokenSource? _measurement;
    private string? _path;
    private long _endedAt;
    private bool _wasStreaming;

    public CinemaForm(TrayApp app)
    {
        _app = app;
        app.Config.Cinema ??= new CinemaSettings();
        Text = L.T("无线影院（实验性）"); Icon = Icons.App; StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new Size(760, 650); MinimumSize = new Size(620, 450);
        BackColor = Theme.P.Window;
        var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.P.Window };
        var body = new StackPanel { Dock = DockStyle.Top, Inset = new Padding(20), Gap = 10 };
        page.Controls.Add(body); Controls.Add(page);
        void FitBody()
        {
            int width = Math.Max(1, page.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
            body.Height = body.GetPreferredSize(new Size(width, 0)).Height;
        }
        page.SizeChanged += (_, _) => FitBody(); body.ControlAdded += (_, _) => FitBody();
        body.Controls.Add(Ui.Header(Text));
        body.Controls.Add(Ui.Note(L.T("把本地 5.1／7.1 文件的独立声道送到不同 HomePod。至少选择前左和前右；未分配的中置与环绕会混入前置。每台应是独立音箱，立体声对暂不支持。")));
        body.Controls.Add(Ui.Note(L.T("这里只播放文件音轨，不显示视频。")));
        var choose = new FluentButton(L.T("选择影音文件"));
        body.Controls.Add(Ui.Row(8, null, choose, _play, _pause, _stop)); body.Controls.Add(_file);
        _devices = app.LastScan.Where(d => d.Model.StartsWith("AudioAccessory", StringComparison.Ordinal)
            && !StereoPairs.IsPairId(d.DeviceId)).GroupBy(d => GroupPlan.Key(d.DeviceId)).Select(g => g.First()).ToList();
        foreach (var saved in app.Config.Cinema.Speakers ?? [])
            if (_devices.All(d => GroupPlan.Key(d.DeviceId) != GroupPlan.Key(saved.DeviceId)))
                _devices.Add(new AirPlayDevice(saved.Name, saved.DeviceId, System.Net.IPAddress.TryParse(saved.Host, out var address) ? address : System.Net.IPAddress.None,
                    7000, "", new Dictionary<string, string>()));
        foreach (var role in Enum.GetValues<CinemaRole>())
        {
            var combo = new FluentComboBox { MinWidth = 210 };
            combo.Items.Add(L.T("（不使用）")); foreach (var device in _devices) combo.Items.Add(device.Name);
            var saved = app.Config.Cinema.Speakers?.FirstOrDefault(s => s.Role == role);
            combo.SelectedIndex = saved == null ? 0 : _devices.FindIndex(d => GroupPlan.Key(d.DeviceId) == GroupPlan.Key(saved.DeviceId)) + 1;
            var delay = Ui.Slider(0, 500, 1, 10); delay.Value = Math.Clamp(saved?.DelayMs ?? 0, 0, 500);
            var value = new TextBlock(delay.Value + " ms");
            delay.Scroll += (_, _) => value.Text = delay.Value + " ms";
            body.Controls.Add(new FieldRow(RoleName(role), Ui.Row(8, delay, combo, delay, value)));
            _rows.Add((role, combo, delay, value));
        }
        _inputs = AudioEndpoints.Inputs(); _microphone.Items.Add(L.T("系统默认")); foreach (var input in _inputs) _microphone.Items.Add(input.Name);
        int mic = _inputs.FindIndex(i => i.Id == app.Config.Cinema.CalibrationMicId);
        if (app.Config.Cinema.CalibrationMicId != null && mic < 0)
        { _microphone.Items.Add(L.T("未发现")); _microphone.SelectedIndex = _microphone.Items.Count - 1; }
        else _microphone.SelectedIndex = mic + 1;
        body.Controls.Add(new FieldRow(L.T("校准麦克风"), Ui.Row(8, _microphone, _microphone, _calibrate)));
        body.Controls.Add(Ui.Note(L.T("播放后可测量：每台音箱依次发出短测试音，麦克风临时测量相对延迟；不保存录音。校准失败会保留原来的设置。")));
        // Keep measurement results readable while the 250 ms playback status continues updating.
        body.Controls.Add(_calibrationStatus);
        _calibrationStatus.TextChanged += (_, _) => FitBody();
        var volume = Ui.Slider(0, (int)app.Config.VolumeCapPercent); volume.Value = Math.Min(volume.Maximum, (int)(app.Config.Volume ?? 20));
        volume.Scroll += (_, _) => app.PreviewVolume(volume.Value);
        body.Controls.Add(new FieldRow(L.T("音量"), volume)); body.Controls.Add(_status);
        choose.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = L.T("影音文件") + "|*.wav;*.mp4;*.m4a;*.mkv;*.wma;*.aac|" + L.T("所有文件") + "|*.*" };
            if (dialog.ShowDialog(this) == DialogResult.OK) { StopPlayback(); _path = dialog.FileName; _file.Text = Path.GetFileName(_path); }
        };
        _play.Click += async (_, _) => await PlayAsync();
        _pause.Click += (_, _) => { if (_source != null) _source.Paused = !_source.Paused; };
        _stop.Click += (_, _) => StopPlayback();
        _calibrate.Click += async (_, _) => await CalibrateAsync();
        _timer.Tick += (_, _) => RefreshStatus(); _timer.Start(); RefreshStatus();
    }
    internal static string RoleName(CinemaRole role) => role switch
    {
        CinemaRole.FrontLeft => L.T("前左"), CinemaRole.FrontRight => L.T("前右"), CinemaRole.Center => L.T("中置"),
        CinemaRole.Subwoofer => L.T("低音声道"), CinemaRole.RearLeft => L.T("后左"), CinemaRole.RearRight => L.T("后右"),
        CinemaRole.SideLeft => L.T("侧左"), _ => L.T("侧右"),
    };
    private CinemaSpeaker[] SelectedLayout() => _rows.Where(r => r.Choice.SelectedIndex > 0).Select(r =>
    {
        var device = _devices[r.Choice.SelectedIndex - 1];
        return new CinemaSpeaker { Role = r.Role, DeviceId = device.DeviceId, Name = device.Name,
            Host = device.Address.Equals(System.Net.IPAddress.None) ? null : device.Address.ToString(), DelayMs = r.Delay.Value };
    }).ToArray();
    private async Task PlayAsync()
    {
        if (_path == null || _measurement != null) return;
        StopPlayback();
        _calibrationStatus.Text = "";
        CinemaFileSource? source = null;
        try
        {
            var layout = SelectedLayout(); CinemaMatrix.Validate(layout);
            source = new CinemaFileSource(_path, layout); _play.Enabled = false;
            if (!await Task.Run(() => source.WaitOpened(TimeSpan.FromSeconds(10)))) throw new IOException(source.Error ?? "Decoder load timed out.");
            if (IsDisposed) { source.Dispose(); return; }
            _activeLayout = layout; _source = source; _endedAt = 0; _wasStreaming = false;
            _app.Config.Cinema.Speakers = layout.ToList(); _app.Config.Save();
            _app.StartCinema(source, layout);
        }
        catch (Exception ex) { source?.Dispose(); if (!IsDisposed) _status.Text = L.F("影院播放失败：{0}", ex.Message); }
        finally { if (!IsDisposed) _play.Enabled = true; }
    }
    private async Task CalibrateAsync()
    {
        if (_source is not { } source || !_app.CinemaStreaming || _measurement != null) return;
        using var cancellation = new CancellationTokenSource(); _measurement = cancellation;
        try
        {
            int choice = _microphone.SelectedIndex;
            string? mic = choice <= 0 ? null : choice <= _inputs.Count ? _inputs[choice - 1].Id : _app.Config.Cinema.CalibrationMicId;
            _calibrationStatus.Text = L.T("正在测量音箱延迟…");
            var readings = await CinemaAcousticCalibrator.MeasureAsync(source, _activeLayout.Length, mic,
                () => ReferenceEquals(_source, source) && _app.CinemaStreaming, cancellation.Token);
            if (IsDisposed || !ReferenceEquals(_source, source)) return;
            var delays = AcousticAlignment.RelativeOffsets(readings);
            for (int i = 0; i < delays.Length; i++)
            {
                _activeLayout[i].DelayMs = delays[i];
                var row = _rows.First(r => r.Role == _activeLayout[i].Role); row.Delay.Value = delays[i]; row.Value.Text = delays[i] + " ms";
            }
            source.ApplyDelays(delays); _app.Config.Cinema.CalibrationMicId = mic; _app.Config.Cinema.Speakers = _activeLayout.ToList(); _app.Config.Save();
            _calibrationStatus.Text = L.T("音箱延迟校准已应用。");
        }
        catch (OperationCanceledException) { if (!IsDisposed) _calibrationStatus.Text = ""; }
        catch (Exception ex) { if (!IsDisposed) _calibrationStatus.Text = L.F("音箱校准失败：{0}", ex.Message); }
        finally { _measurement = null; }
    }
    private void RefreshStatus()
    {
        bool active = _source != null;
        _pause.Enabled = _stop.Enabled = active;
        _calibrate.Enabled = active && _app.CinemaStreaming && _measurement == null;
        foreach (var row in _rows) { row.Choice.Enabled = !active; row.Delay.Enabled = !active; }
        if (_measurement != null) return;
        if (_source is { } source)
        {
            if (_app.CinemaStreaming) _wasStreaming = true;
            else if (_wasStreaming && _app.Controller.State == StreamState.Idle) { StopPlayback(); return; }
            _status.Text = source.Error != null ? L.F("影院播放失败：{0}", source.Error)
                : L.F("影院：{0} · {1} 声道 · {2:F1} 秒", _app.Controller.StatusText, source.Channels, source.PositionSeconds);
            if (source.Error != null) StopPlayback();
            else if (source.Finished)
            {
                if (_endedAt == 0) _endedAt = Environment.TickCount64 + _app.Controller.EffectiveLatencyMs + 150;
                if (Environment.TickCount64 >= _endedAt) StopPlayback();
            }
        }
    }
    private void StopPlayback()
    {
        _measurement?.Cancel();
        var source = _source; _source = null;
        if (source != null) _app.StopCinema(source);
    }
    protected override void OnFormClosed(FormClosedEventArgs e) { StopPlayback(); _timer.Dispose(); base.OnFormClosed(e); }
}
