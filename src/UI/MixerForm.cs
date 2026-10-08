using HomePodCast.Audio;

namespace HomePodCast.UI;

/// <summary>
/// Windows-style volume mixer for everything that is going to the speaker: one row per app with a
/// live level meter, volume slider and mute, plus the speaker's own volume on top.
/// </summary>
internal sealed class MixerForm : Form
{
    private const int NameWidth = 170, MeterWidth = 110, SliderWidth = 200;

    private readonly TrayApp _app;
    private readonly TableLayoutPanel _rows;
    private readonly TrackBar _master;
    private readonly Label _masterValue;
    private readonly Label _empty;
    private readonly System.Windows.Forms.Timer _meterTimer = new() { Interval = 50 };
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 1500 };
    private readonly Dictionary<string, Row> _byKey = new();
    private List<AppAudio> _apps = [];

    private sealed record Row(AppAudio App, Control[] Controls, Meter Meter, TrackBar Slider, Label Value, CheckBox Mute);

    public MixerForm(TrayApp app)
    {
        _app = app;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9f);
        Text = "混音器";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = Icons.Speaker(Icons.Streaming);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var outer = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(14, 12, 14, 12),
        };

        // Speaker volume
        var head = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 0, 0, 6) };
        head.Controls.Add(new Label
        {
            Text = $"{_app.Config.DeviceName ?? "HomePod"} 音量",
            AutoSize = false,
            Size = new Size(NameWidth + 30 + MeterWidth, 24),
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font, FontStyle.Bold),
        }, 0, 0);
        _master = new TrackBar { Minimum = 0, Maximum = 100, TickStyle = TickStyle.None, AutoSize = false, Size = new Size(SliderWidth, 28) };
        _masterValue = new Label { AutoSize = false, Size = new Size(36, 24), TextAlign = ContentAlignment.MiddleRight };
        head.Controls.Add(_master, 1, 0);
        head.Controls.Add(_masterValue, 2, 0);
        _master.Value = (int)Math.Round(_app.Controller.Volume ?? _app.Config.Volume ?? 0);
        _masterValue.Text = _master.Value.ToString();
        var masterDebounce = new System.Windows.Forms.Timer { Interval = 150 };
        _master.ValueChanged += (_, _) => { _masterValue.Text = _master.Value.ToString(); masterDebounce.Stop(); masterDebounce.Start(); };
        masterDebounce.Tick += (_, _) => { masterDebounce.Stop(); _app.SetVolume(_master.Value); };

        var caption = new Label
        {
            Text = "应用（推送到音箱的声音）",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(3, 8, 3, 4),
        };
        _rows = new TableLayoutPanel { AutoSize = true, ColumnCount = 7 };
        _empty = new Label { Text = "现在没有程序在发声。", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 6, 3, 6) };
        var note = new Label
        {
            Text = "和 Windows 音量合成器是同一套设置，系统会记住每个程序的音量。",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(3, 10, 3, 0),
        };

        outer.Controls.AddRange([head, BuildRoutingPanel(), caption, _rows, _empty, note]);
        Controls.Add(outer);
        ResumeLayout(false);
        PerformLayout();

        _meterTimer.Tick += (_, _) => UpdateMeters();
        _refreshTimer.Tick += (_, _) => { RefreshApps(); SyncRoutes(); };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RefreshApps();
        SyncRoutes();
        _meterTimer.Start();
        _refreshTimer.Start();
    }

    private void RefreshApps()
    {
        var fresh = AppAudio.Enumerate();
        var keys = fresh.Select(a => a.Key).ToList();
        bool changed = !keys.SequenceEqual(_apps.Select(a => a.Key));
        if (!changed)
        {
            foreach (var a in fresh) a.Dispose();
            SyncValues();
            return;
        }

        SuspendLayout();
        _rows.SuspendLayout();
        foreach (var a in _apps) a.Dispose();
        _rows.Controls.Clear();
        _rows.RowStyles.Clear();
        _byKey.Clear();
        _routeCells.Clear();
        _apps = fresh;
        for (int i = 0; i < fresh.Count; i++) AddRow(fresh[i], i);
        _empty.Visible = fresh.Count == 0;
        _rows.ResumeLayout();
        ResumeLayout();
    }

    private void AddRow(AppAudio app, int index)
    {
        var icon = new PictureBox
        {
            Size = new Size(20, 20),
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = app.Icon?.ToBitmap(),
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 3, 6, 3),
        };
        var name = new Label
        {
            Text = app.Name,
            AutoSize = false,
            AutoEllipsis = true,
            Size = new Size(NameWidth, 24),
            TextAlign = ContentAlignment.MiddleLeft,
            Anchor = AnchorStyles.Left,
        };
        var meter = new Meter { Size = new Size(MeterWidth, 8), Anchor = AnchorStyles.Left, Margin = new Padding(3, 3, 10, 3) };
        var slider = new TrackBar
        {
            Minimum = 0,
            Maximum = 100,
            TickStyle = TickStyle.None,
            AutoSize = false,
            Size = new Size(SliderWidth, 28),
            Value = (int)Math.Round(app.Volume * 100),
        };
        var value = new Label { AutoSize = false, Size = new Size(36, 24), TextAlign = ContentAlignment.MiddleRight, Text = slider.Value.ToString() };
        var mute = new CheckBox
        {
            Appearance = Appearance.Button,
            Text = "静音",
            AutoSize = true,
            Checked = app.Muted,
            Margin = new Padding(8, 2, 3, 2),
        };
        slider.ValueChanged += (_, _) =>
        {
            value.Text = slider.Value.ToString();
            try { app.Volume = slider.Value / 100f; } catch (Exception ex) { Log.Warn($"mixer: {ex.Message}"); }
        };
        mute.CheckedChanged += (_, _) =>
        {
            try { app.Muted = mute.Checked; } catch (Exception ex) { Log.Warn($"mixer: {ex.Message}"); }
            mute.ForeColor = mute.Checked ? Icons.Error : SystemColors.ControlText;
        };
        mute.ForeColor = mute.Checked ? Icons.Error : SystemColors.ControlText;

        Control[] cells = [icon, name, meter, slider, value, mute, RouteCell(app)];
        for (int c = 0; c < cells.Length; c++) _rows.Controls.Add(cells[c], c, index);
        _rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _byKey[app.Key] = new Row(app, cells, meter, slider, value, mute);
    }

    /// <summary>Pick up changes made elsewhere (e.g. in the Windows mixer) without fighting the user.</summary>
    private void SyncValues()
    {
        foreach (var row in _byKey.Values)
        {
            try
            {
                if (!row.Slider.Capture)
                {
                    int v = (int)Math.Round(row.App.Volume * 100);
                    if (v != row.Slider.Value) row.Slider.Value = v;
                }
                bool m = row.App.Muted;
                if (m != row.Mute.Checked) row.Mute.Checked = m;
            }
            catch { }
        }
    }

    private void UpdateMeters()
    {
        foreach (var row in _byKey.Values)
        {
            try { row.Meter.SetLevel(row.App.Muted ? 0 : row.App.Peak); } catch { }
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _meterTimer.Stop();
        _refreshTimer.Stop();
        foreach (var a in _apps) a.Dispose();
        _apps = [];
        base.OnFormClosed(e);
    }

    // ---------------------------------------------------------------- per-app routing

    private static readonly AudioRoute[] Routes = [AudioRoute.HomePod, AudioRoute.Local, AudioRoute.Both];
    private const int RouteWidth = 104;

    private readonly Dictionary<string, Control> _routeCells = new(); // by AppAudio.Key
    private readonly ToolTip _routeTips = new();
    private ComboBox? _routeDefault;
    private Label? _routeStatus;
    private bool _syncingRoutes;

    private static string RouteName(AudioRoute route) => route switch
    {
        AudioRoute.Local => L.T("本机"),
        AudioRoute.Both => L.T("两者"),
        _ => "HomePod",
    };

    /// <summary>Default destination for apps without their own choice, plus what routing currently does.</summary>
    private Control BuildRoutingPanel()
    {
        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 0) };
        var line = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        line.Controls.Add(new Label { Text = L.T("没单独设置的程序送到"), AutoSize = true, Margin = new Padding(3, 7, 6, 3) });
        _routeDefault = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = RouteWidth, Enabled = AppRouting.Supported };
        foreach (var r in Routes) _routeDefault.Items.Add(RouteName(r));
        _routeDefault.SelectedIndex = Array.IndexOf(Routes, _app.Routing.Default);
        _routeDefault.SelectedIndexChanged += (_, _) =>
        {
            if (_syncingRoutes || _routeDefault.SelectedIndex < 0) return;
            _app.Routing.Default = Routes[_routeDefault.SelectedIndex];
            SyncRoutes();
        };
        line.Controls.Add(_routeDefault);
        _routeStatus = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(NameWidth + MeterWidth + SliderWidth + 200, 0),
            ForeColor = Color.DimGray,
            Margin = new Padding(3, 2, 3, 0),
        };
        panel.Controls.Add(line);
        panel.Controls.Add(_routeStatus);
        return panel;
    }

    /// <summary>Destination dropdown for one app row: follow the default, HomePod, this PC or both.</summary>
    private Control RouteCell(AppAudio app)
    {
        Control cell;
        if (app.IsSystemSounds || app.ExeKey.Length == 0)
        {
            cell = new Label
            {
                AutoSize = false,
                Size = new Size(RouteWidth, 24),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.DimGray,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(8, 2, 3, 2),
            };
            _routeTips.SetToolTip(cell, app.IsSystemSounds
                ? L.T("系统声音不属于某一个程序，不能单独设置：按程序分流时只在本机播放。")
                : L.T("读不到这个程序的文件名，不能单独设置。"));
        }
        else
        {
            var box = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = RouteWidth,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(8, 2, 3, 2),
                Enabled = AppRouting.Supported,
            };
            box.Items.Add(DefaultRouteText());
            foreach (var r in Routes) box.Items.Add(RouteName(r));
            box.SelectedIndex = _app.Routing.Get(app.ExeKey) is { } own ? Array.IndexOf(Routes, own) + 1 : 0;
            box.SelectedIndexChanged += (_, _) =>
            {
                if (_syncingRoutes || box.SelectedIndex < 0) return;
                _app.Routing.Set(app.ExeKey, box.SelectedIndex == 0 ? null : Routes[box.SelectedIndex - 1]);
                SyncRoutes();
            };
            _routeTips.SetToolTip(box, L.T("HomePod：只在音箱播放，本机静音\n本机：只在这台电脑播放，不推送\n两者：音箱和本机都播放"));
            cell = box;
        }
        _routeCells[app.Key] = cell;
        return cell;
    }

    private string DefaultRouteText() => L.F("默认（{0}）", RouteName(_app.Routing.Default));

    /// <summary>Show the current rules (also for other rows of the same program) and the routing status.</summary>
    private void SyncRoutes()
    {
        if (_routeDefault == null || _routeStatus == null) return;
        var routing = _app.Routing;
        _syncingRoutes = true;
        try
        {
            int d = Array.IndexOf(Routes, routing.Default);
            if (_routeDefault.SelectedIndex != d) _routeDefault.SelectedIndex = d;
            string defaultText = DefaultRouteText();
            foreach (var row in _byKey.Values)
            {
                if (!_routeCells.TryGetValue(row.App.Key, out var cell)) continue;
                if (cell is ComboBox box)
                {
                    if (box.Items[0] as string != defaultText) box.Items[0] = defaultText;
                    int index = routing.Get(row.App.ExeKey) is { } own ? Array.IndexOf(Routes, own) + 1 : 0;
                    if (box.SelectedIndex != index) box.SelectedIndex = index;
                }
                else
                {
                    cell.Text = row.App.IsSystemSounds && routing.Active ? L.T("本机") : "—";
                }
            }
        }
        finally
        {
            _syncingRoutes = false;
        }
        _routeStatus.Text = RouteStatus();
    }

    private string RouteStatus()
    {
        var routing = _app.Routing;
        if (!AppRouting.Supported)
            return L.T("按程序分流需要 Windows 10 2004 或更高版本，现在所有声音都推送到音箱。");
        if (!routing.Active)
            return L.T("所有程序都推送到音箱，延迟最低；本机出不出声由 Windows 音量决定。");
        var lines = new List<string>
        {
            L.F("已按程序分流：推送到音箱的声音多约 {0} ms 延迟，设为「HomePod」的程序在本机静音。", RoutedCapture.RoutedExtraLatencyMs),
        };
        if (_app.Controller.State == StreamState.Idle) lines.Add(L.T("连接音箱后生效。"));
        bool localWanted = routing.Default != AudioRoute.HomePod || routing.Rules.Apps.Values.Any(r => r != AudioRoute.HomePod);
        if (localWanted && AppRouting.LocalOutputSilent())
            lines.Add(L.T("Windows 输出已静音：设为「本机」或「两者」的程序在这台电脑上也听不到。"));
        return string.Join("\n", lines);
    }

    private sealed class Meter : Control
    {
        private float _level, _shown;

        public Meter()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        public void SetLevel(float value)
        {
            // Fast attack, slow release, perceptual (sqrt) scale.
            _level = (float)Math.Sqrt(Math.Clamp(value, 0f, 1f));
            _shown = _level > _shown ? _level : _shown * 0.85f + _level * 0.15f;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using var bg = new SolidBrush(Color.FromArgb(225, 225, 225));
            e.Graphics.FillRectangle(bg, ClientRectangle);
            int w = (int)(Width * _shown);
            if (w <= 0) return;
            using var fg = new SolidBrush(_shown > 0.95f ? Icons.Error : Icons.Streaming);
            e.Graphics.FillRectangle(fg, 0, 0, w, Height);
        }
    }
}
