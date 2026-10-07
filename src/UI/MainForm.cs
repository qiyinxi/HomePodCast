using HomePodCast.Net;

namespace HomePodCast.UI;

internal sealed class MainForm : Form
{
    public static readonly (int Ms, string Label)[] Latencies =
    [
        (80, "80 ms · 极限"),
        (100, "100 ms · 推荐（游戏）"),
        (120, "120 ms"),
        (150, "150 ms"),
        (200, "200 ms · Wi-Fi 较差时"),
        (300, "300 ms"),
        (500, "500 ms · 最稳"),
    ];

    private readonly TrayApp _app;
    private readonly ComboBox _device = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button _refresh = new() { Text = "刷新" };
    private readonly Panel _dot = new() { Size = new Size(12, 12) };
    private readonly Label _status = new() { AutoSize = false, AutoEllipsis = true };
    private readonly Button _connect = new() { Text = "连接" };
    private readonly TrackBar _volume = new() { Minimum = 0, Maximum = 100, TickFrequency = 10, SmallChange = 1, LargeChange = 5 };
    private readonly Label _volumeValue = new() { TextAlign = ContentAlignment.MiddleRight };
    private readonly ComboBox _latency = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _autostart = new() { Text = "开机自动启动", AutoSize = true };
    private readonly CheckBox _autoconnect = new() { Text = "启动后自动连接", AutoSize = true };
    private readonly Button _syncTest = new() { Text = "音画同步测试…" };
    private readonly Label _stats = new() { AutoSize = false, ForeColor = Color.DimGray };
    private readonly System.Windows.Forms.Timer _statsTimer = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer _volumeDebounce = new() { Interval = 150 };
    private List<AirPlayDevice> _devices = [];
    private bool _loading;

    public MainForm(TrayApp app)
    {
        _app = app;
        Text = "HomePod 音响";
        Font = new Font("Microsoft YaHei UI", 9.5f);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Icon = Icons.Speaker(Icons.Streaming);
        ClientSize = new Size(440, 330);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 14, 16, 10),
            ColumnCount = 3,
            RowCount = 8,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));

        _device.Dock = DockStyle.Fill;
        _refresh.Dock = DockStyle.Fill;
        layout.Controls.Add(Caption("音箱"), 0, 0);
        layout.Controls.Add(_device, 1, 0);
        layout.Controls.Add(_refresh, 2, 0);

        var statusRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 8, 0, 4) };
        _dot.Margin = new Padding(2, 6, 6, 0);
        _dot.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var b = new SolidBrush(Icons.For(_app.Controller.State));
            e.Graphics.FillEllipse(b, 0, 0, 11, 11);
        };
        _status.Size = new Size(370, 24);
        _status.TextAlign = ContentAlignment.MiddleLeft;
        statusRow.Controls.AddRange([_dot, _status]);
        layout.Controls.Add(statusRow, 0, 1);
        layout.SetColumnSpan(statusRow, 3);

        _connect.Dock = DockStyle.Fill;
        _connect.Height = 36;
        _connect.Font = new Font(Font.FontFamily, 10.5f);
        layout.Controls.Add(_connect, 0, 2);
        layout.SetColumnSpan(_connect, 3);

        _volume.Dock = DockStyle.Fill;
        _volumeValue.Dock = DockStyle.Fill;
        layout.Controls.Add(Caption("音量"), 0, 3);
        layout.Controls.Add(_volume, 1, 3);
        layout.Controls.Add(_volumeValue, 2, 3);

        foreach (var (_, label) in Latencies) _latency.Items.Add(label);
        _latency.Dock = DockStyle.Fill;
        layout.Controls.Add(Caption("延迟"), 0, 4);
        layout.Controls.Add(_latency, 1, 4);
        layout.SetColumnSpan(_latency, 2);

        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
        _autoconnect.Margin = new Padding(18, 3, 3, 3);
        options.Controls.AddRange([_autostart, _autoconnect]);
        layout.Controls.Add(options, 0, 5);
        layout.SetColumnSpan(options, 3);

        _syncTest.AutoSize = true;
        layout.Controls.Add(_syncTest, 0, 6);
        layout.SetColumnSpan(_syncTest, 3);

        _stats.Dock = DockStyle.Fill;
        layout.Controls.Add(_stats, 0, 7);
        layout.SetColumnSpan(_stats, 3);

        for (int i = 0; i < 8; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);

        _refresh.Click += (_, _) => _app.RefreshDevices();
        _connect.Click += (_, _) => _app.ToggleConnection();
        _device.SelectedIndexChanged += (_, _) =>
        {
            if (!_loading && _device.SelectedIndex >= 0 && _device.SelectedIndex < _devices.Count)
                _app.SelectDevice(_devices[_device.SelectedIndex]);
        };
        _volume.ValueChanged += (_, _) =>
        {
            _volumeValue.Text = _volume.Value.ToString();
            if (_loading) return;
            _volumeDebounce.Stop();
            _volumeDebounce.Start();
        };
        _volumeDebounce.Tick += (_, _) =>
        {
            _volumeDebounce.Stop();
            _app.SetVolume(_volume.Value);
        };
        _latency.SelectedIndexChanged += (_, _) =>
        {
            if (!_loading && _latency.SelectedIndex >= 0) _app.SetLatency(Latencies[_latency.SelectedIndex].Ms);
        };
        _autostart.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            try { Autostart.Enabled = _autostart.Checked; }
            catch (Exception ex) { MessageBox.Show(this, $"设置开机启动失败：{ex.Message}", Text); }
        };
        _autoconnect.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            _app.Config.AutoConnect = _autoconnect.Checked;
            _app.Config.Save();
        };
        _syncTest.Click += (_, _) => _app.RunSyncTest(this);
        _statsTimer.Tick += (_, _) => UpdateStats();

        LoadFromConfig();
    }

    private static Label Caption(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
    };

    private void LoadFromConfig()
    {
        _loading = true;
        var cfg = _app.Config;
        int li = Array.FindIndex(Latencies, l => l.Ms == cfg.LatencyMs);
        _latency.SelectedIndex = li >= 0 ? li : 1;
        _volume.Value = (int)Math.Round(_app.Controller.Volume ?? cfg.Volume ?? 0);
        _volumeValue.Text = cfg.Volume is null && _app.Controller.Volume is null ? "--" : _volume.Value.ToString();
        _autostart.Checked = Autostart.Enabled;
        _autoconnect.Checked = cfg.AutoConnect;
        _loading = false;
    }

    public void SetDevices(List<AirPlayDevice> devices)
    {
        _loading = true;
        _devices = devices;
        _device.Items.Clear();
        foreach (var d in devices)
            _device.Items.Add(string.IsNullOrEmpty(d.Model) ? d.Name : $"{d.Name}（{FriendlyModel(d.Model)}）");
        var cfgId = _app.Config.DeviceId;
        _device.SelectedIndex = devices.FindIndex(d =>
            cfgId != null && StreamController.Normalize(d.DeviceId).Equals(StreamController.Normalize(cfgId), StringComparison.OrdinalIgnoreCase));
        _loading = false;
    }

    public void SetScanning(bool scanning)
    {
        _refresh.Enabled = !scanning;
        _refresh.Text = scanning ? "…" : "刷新";
    }

    private static string FriendlyModel(string model) => model switch
    {
        _ when model.StartsWith("AudioAccessory6") => "HomePod 第二代",
        _ when model.StartsWith("AudioAccessory5") => "HomePod mini",
        _ when model.StartsWith("AudioAccessory1") => "HomePod",
        _ when model.StartsWith("AppleTV") => "Apple TV",
        _ => model,
    };

    public void UpdateState()
    {
        var c = _app.Controller;
        _status.Text = c.StatusText;
        _dot.Invalidate();
        _connect.Text = c.State == StreamState.Idle ? "连接" : "断开";
        if (c.State == StreamState.Streaming && c.Volume is { } v && !_volume.Capture)
        {
            _loading = true;
            _volume.Value = (int)Math.Round(v);
            _volumeValue.Text = _volume.Value.ToString();
            _loading = false;
        }
        UpdateStats();
    }

    private void UpdateStats()
    {
        var c = _app.Controller;
        var cap = c.Capture;
        var s = c.Client?.Sender;
        var source = cap?.DeviceName is { } name ? $"音源：{name}" : "音源：默认输出设备";
        if (s == null || c.State != StreamState.Streaming)
        {
            _stats.Text = source;
            return;
        }
        double fifoMs = c.Fifo.Depth * 1000.0 / RtpSender.SampleRate;
        _stats.Text = $"{source}\n缓冲 {fifoMs:F0} ms · 迟发 {s.LateWakeups} · 断音 {c.Fifo.Underruns} · " +
                      $"重传 {s.Retransmitted}/{s.RetransmitRequests}";
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) { UpdateState(); _statsTimer.Start(); }
        else _statsTimer.Stop();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true; // closing the window only hides it; quit from the tray menu
            Hide();
            _app.ShowHiddenHint();
            return;
        }
        base.OnFormClosing(e);
    }
}
