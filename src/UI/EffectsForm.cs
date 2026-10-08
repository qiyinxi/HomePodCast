using HomePodCast.Audio;

namespace HomePodCast.UI;

/// <summary>
/// 麦克风与音效: the microphone (device, on/off, gain, noise gate), where the voice goes (HomePod / local
/// monitor / both), reverb, and two equalizers: one on the mic, one on everything sent to the HomePod.
/// Edits AppConfig.Effects and applies every change to a <see cref="MicEffects"/> immediately.
/// </summary>
internal sealed class EffectsForm : Form
{
    private const int FieldWidth = 230, TextWidth = 400;
    private static readonly Color Warning = Color.FromArgb(176, 96, 0);

    private readonly AppConfig _config;
    private readonly MicEffects _fx;
    private readonly bool _ownsEngine;

    private readonly ComboBox _micDevice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = FieldWidth };
    private readonly Button _refresh = new() { Text = L.T("刷新"), Size = new Size(64, 27), Anchor = AnchorStyles.Left };
    private readonly CheckBox _micOn = new() { Text = L.T("开启麦克风"), AutoSize = true };
    private readonly LevelMeter _meter = new() { Size = new Size(140, 8), Anchor = AnchorStyles.Left, Margin = new Padding(14, 9, 3, 3) };
    private readonly TrackBar _gain = Slider(-12, 30);
    private readonly Label _gainValue = Value();
    private readonly CheckBox _gate = new() { Text = L.T("噪声门"), AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly TrackBar _gateThreshold = Slider(-80, -20);
    private readonly Label _gateValue = Value();
    private readonly RadioButton _toHomePod = new() { Text = "HomePod", AutoSize = true };
    private readonly RadioButton _toMonitor = new() { Text = L.T("本机监听"), AutoSize = true };
    private readonly RadioButton _toBoth = new() { Text = L.T("两者"), AutoSize = true };
    private readonly ComboBox _monitorDevice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = FieldWidth };
    private readonly Label _howl = Note();
    private readonly Label _delayHint = Note();
    private readonly Label _micStatus = Note();
    private readonly Label _routeStatus = Note();

    private readonly CheckBox _reverbOn = new() { Text = L.T("开启混响"), AutoSize = true };
    private readonly TrackBar _room = Slider(0, 100), _damping = Slider(0, 100), _mix = Slider(0, 100), _preDelay = Slider(0, 200);
    private readonly Label _roomValue = Value(), _dampingValue = Value(), _mixValue = Value(), _preDelayValue = Value();

    private readonly EqEditor _micEq, _outputEq;
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 100 };
    private readonly System.Windows.Forms.Timer _saveDebounce = new() { Interval = 600 };
    private List<AudioEndpoint> _inputs = [], _outputs = [];
    private AudioEndpoint? _defaultOutput;
    private bool _loading;

    private EffectsSettings S => _config.Effects;

    public EffectsForm(AppConfig config, MicEffects fx, bool ownsEngine = false)
    {
        _config = config;
        _fx = fx;
        _ownsEngine = ownsEngine;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9f);
        Text = L.T("麦克风与音效");
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = Icons.Speaker(Icons.Streaming);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _micEq = new EqEditor(L.T("麦克风均衡"));
        _outputEq = new EqEditor(L.T("HomePod 输出均衡（所有声音）"));

        var left = Column(BuildMicGroup(), BuildReverbGroup());
        var right = Column(_micEq.Box, _outputEq.Box);
        var root = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Padding = new Padding(8, 6, 8, 8) };
        root.Controls.Add(left, 0, 0);
        root.Controls.Add(right, 1, 0);
        Controls.Add(root);
        ResumeLayout(false);
        PerformLayout();

        WireEvents();
        LoadDevices();
        LoadFromSettings();
        _statusTimer.Tick += (_, _) => UpdateStatus();
    }

    /// <summary>For <c>gui --effects</c>: the form on its own (no tray, no streaming), with its own engine.</summary>
    public static ApplicationContext Standalone()
    {
        var config = AppConfig.Load();
        config.Effects ??= new EffectsSettings();
        return new ApplicationContext(new EffectsForm(config, new MicEffects(config.Effects), ownsEngine: true));
    }

    // ---------------------------------------------------------------- layout

    private GroupBox BuildMicGroup()
    {
        var t = Table(3);
        t.Controls.Add(Caption(L.T("设备")), 0, 0);
        t.Controls.Add(_micDevice, 1, 0);
        t.Controls.Add(_refresh, 2, 0);

        var onRow = Flow(_micOn, _meter);
        t.Controls.Add(onRow, 1, 1);
        t.SetColumnSpan(onRow, 2);

        t.Controls.Add(Caption(L.T("增益")), 0, 2);
        t.Controls.Add(_gain, 1, 2);
        t.Controls.Add(_gainValue, 2, 2);

        t.Controls.Add(_gate, 0, 3);
        t.Controls.Add(_gateThreshold, 1, 3);
        t.Controls.Add(_gateValue, 2, 3);

        _toMonitor.Margin = _toBoth.Margin = new Padding(12, 3, 3, 3);
        var route = Flow(_toHomePod, _toMonitor, _toBoth);
        t.Controls.Add(Caption(L.T("送到")), 0, 4);
        t.Controls.Add(route, 1, 4);
        t.SetColumnSpan(route, 2);

        t.Controls.Add(Caption(L.T("监听设备")), 0, 5);
        t.Controls.Add(_monitorDevice, 1, 5);

        _howl.Font = new Font(Font, FontStyle.Bold);
        int row = 6;
        foreach (var note in new[] { _howl, _delayHint, _micStatus, _routeStatus })
        {
            t.Controls.Add(note, 0, row);
            t.SetColumnSpan(note, 3);
            row++;
        }
        _delayHint.Text = L.F("经 HomePod 播出的人声会比实际晚约 {0} ms（推流延迟），唱歌请用「本机监听」并戴耳机。",
            Math.Max(_config.LatencyMs, 100) + 20);
        return Group(L.T("麦克风"), t);
    }

    private GroupBox BuildReverbGroup()
    {
        var t = Table(3);
        t.Controls.Add(_reverbOn, 0, 0);
        t.SetColumnSpan(_reverbOn, 3);
        (string, TrackBar, Label)[] rows =
        [
            (L.T("房间大小"), _room, _roomValue),
            (L.T("阻尼"), _damping, _dampingValue),
            (L.T("混响比例"), _mix, _mixValue),
            (L.T("预延迟"), _preDelay, _preDelayValue),
        ];
        for (int i = 0; i < rows.Length; i++)
        {
            t.Controls.Add(Caption(rows[i].Item1), 0, i + 1);
            t.Controls.Add(rows[i].Item2, 1, i + 1);
            t.Controls.Add(rows[i].Item3, 2, i + 1);
        }
        return Group(L.T("混响"), t);
    }

    private static FlowLayoutPanel Column(params Control[] children)
    {
        var f = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        foreach (var c in children) c.Anchor = AnchorStyles.Left | AnchorStyles.Right; // same width as the widest
        f.Controls.AddRange(children);
        return f;
    }

    private static FlowLayoutPanel Flow(params Control[] children)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, Anchor = AnchorStyles.Left };
        f.Controls.AddRange(children);
        return f;
    }

    private static TableLayoutPanel Table(int columns)
    {
        var t = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = columns, Dock = DockStyle.Fill };
        for (int i = 0; i < columns; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return t;
    }

    private static GroupBox Group(string title, Control content)
    {
        var g = new GroupBox
        {
            Text = title,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 4, 8, 6),
            Margin = new Padding(4),
        };
        g.Controls.Add(content);
        return g;
    }

    private static Label Caption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 0, 8, 0),
    };

    private static Label Value() => new()
    {
        AutoSize = true,
        MinimumSize = new Size(52, 0),
        Anchor = AnchorStyles.Left,
        TextAlign = ContentAlignment.MiddleLeft,
    };

    private static Label Note() => new()
    {
        AutoSize = true,
        MaximumSize = new Size(TextWidth, 0),
        ForeColor = Color.DimGray,
        Margin = new Padding(3, 4, 3, 0),
    };

    private static TrackBar Slider(int min, int max) => new()
    {
        Minimum = min,
        Maximum = max,
        TickStyle = TickStyle.None,
        AutoSize = false,
        Size = new Size(FieldWidth, 28),
        SmallChange = 1,
        LargeChange = Math.Max(1, (max - min) / 10),
        Anchor = AnchorStyles.Left,
    };

    // ---------------------------------------------------------------- settings

    private void WireEvents()
    {
        _refresh.Click += (_, _) => LoadDevices();
        _micOn.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            if (_micOn.Checked && MonitorsSpeakers(micOn: true) && !ConfirmSpeakers())
            {
                _loading = true;
                _micOn.Checked = false;
                _loading = false;
                return;
            }
            _fx.MicOn = _micOn.Checked; // not saved: the mic is always off when the app starts
            UpdateEnabled();
            UpdateStatus();
        };
        _micDevice.SelectedIndexChanged += (_, _) => Edit(() => S.MicDeviceId = Pick(_micDevice, _inputs, S.MicDeviceId));
        _monitorDevice.SelectedIndexChanged += (_, _) => Edit(() => S.MonitorDeviceId = Pick(_monitorDevice, _outputs, S.MonitorDeviceId));
        _gain.ValueChanged += (_, _) => Edit(() => S.MicGainDb = _gain.Value);
        _gate.CheckedChanged += (_, _) => Edit(() => S.GateEnabled = _gate.Checked);
        _gateThreshold.ValueChanged += (_, _) => Edit(() => S.GateThresholdDb = _gateThreshold.Value);
        foreach (var radio in new[] { _toHomePod, _toMonitor, _toBoth })
            radio.CheckedChanged += (_, _) =>
            {
                if (!radio.Checked) return;
                Edit(() => S.Destination = _toMonitor.Checked ? MicDestination.Monitor
                    : _toBoth.Checked ? MicDestination.Both : MicDestination.HomePod);
            };
        _reverbOn.CheckedChanged += (_, _) => Edit(() => S.ReverbEnabled = _reverbOn.Checked);
        _room.ValueChanged += (_, _) => Edit(() => S.ReverbRoomSize = _room.Value / 100.0);
        _damping.ValueChanged += (_, _) => Edit(() => S.ReverbDamping = _damping.Value / 100.0);
        _mix.ValueChanged += (_, _) => Edit(() => S.ReverbMix = _mix.Value / 100.0);
        _preDelay.ValueChanged += (_, _) => Edit(() => S.ReverbPreDelayMs = _preDelay.Value);
        _micEq.Changed += () => Edit(() => { });
        _outputEq.Changed += () => Edit(() => { });
        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce.Stop();
            _config.Save();
        };
    }

    /// <summary>Apply a UI change to the settings and the engine; save shortly after the last change.</summary>
    private void Edit(Action change)
    {
        if (_loading) return;
        bool before = MonitorsSpeakers(_fx.MicOn);
        var (destination, monitorId) = (S.Destination, S.MonitorDeviceId);
        change();
        if (!before && MonitorsSpeakers(_fx.MicOn) && !ConfirmSpeakers())
        {
            (S.Destination, S.MonitorDeviceId) = (destination, monitorId);
            LoadDevices();
            LoadFromSettings();
            return;
        }
        _fx.Apply();
        ShowValues();
        UpdateEnabled();
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private void LoadDevices()
    {
        _loading = true;
        _inputs = AudioEndpoints.Inputs();
        _outputs = AudioEndpoints.Outputs();
        _defaultOutput = AudioEndpoints.DefaultOutput();
        Fill(_micDevice, L.T("默认麦克风"), _inputs, S.MicDeviceId);
        Fill(_monitorDevice, L.T("默认输出设备"), _outputs, S.MonitorDeviceId);
        _loading = false;
        UpdateEnabled();
    }

    /// <summary>Id for the selected entry: null for the default, unchanged for the "disconnected" placeholder.</summary>
    private static string? Pick(ComboBox box, List<AudioEndpoint> devices, string? current) =>
        box.SelectedIndex <= 0 ? null : box.SelectedIndex - 1 < devices.Count ? devices[box.SelectedIndex - 1].Id : current;

    private static void Fill(ComboBox box, string defaultText, List<AudioEndpoint> devices, string? selectedId)
    {
        box.Items.Clear();
        box.Items.Add(defaultText);
        foreach (var d in devices) box.Items.Add(d.Name);
        int index = devices.FindIndex(d => d.Id == selectedId);
        if (index < 0 && selectedId != null)
        {
            box.Items.Add(L.T("（已断开的设备）"));  // keep the saved choice; never fall back silently
            box.SelectedIndex = box.Items.Count - 1;
            return;
        }
        box.SelectedIndex = index + 1;
    }

    private void LoadFromSettings()
    {
        _loading = true;
        var s = S;
        _micOn.Checked = _fx.MicOn;
        _gain.Value = Math.Clamp((int)Math.Round(s.MicGainDb), _gain.Minimum, _gain.Maximum);
        _gate.Checked = s.GateEnabled;
        _gateThreshold.Value = Math.Clamp((int)Math.Round(s.GateThresholdDb), _gateThreshold.Minimum, _gateThreshold.Maximum);
        _toHomePod.Checked = s.Destination == MicDestination.HomePod;
        _toMonitor.Checked = s.Destination == MicDestination.Monitor;
        _toBoth.Checked = s.Destination == MicDestination.Both;
        _reverbOn.Checked = s.ReverbEnabled;
        _room.Value = Percent(s.ReverbRoomSize);
        _damping.Value = Percent(s.ReverbDamping);
        _mix.Value = Percent(s.ReverbMix);
        _preDelay.Value = Math.Clamp((int)Math.Round(s.ReverbPreDelayMs), 0, _preDelay.Maximum);
        s.MicEq ??= new EqSettings();
        s.OutputEq ??= new EqSettings();
        _micEq.Load(s.MicEq);
        _outputEq.Load(s.OutputEq);
        _loading = false;
        ShowValues();
        UpdateEnabled();
        UpdateStatus();
    }

    private static int Percent(double v) => Math.Clamp((int)Math.Round(v * 100), 0, 100);

    private void ShowValues()
    {
        _gainValue.Text = $"{_gain.Value:+0;-0;0} dB";
        _gateValue.Text = $"{_gateThreshold.Value} dB";
        _roomValue.Text = $"{_room.Value}%";
        _dampingValue.Text = $"{_damping.Value}%";
        _mixValue.Text = $"{_mix.Value}%";
        _preDelayValue.Text = $"{_preDelay.Value} ms";
    }

    private void UpdateEnabled()
    {
        bool monitor = S.Destination != MicDestination.HomePod;
        _monitorDevice.Enabled = monitor;
        _gateThreshold.Enabled = _gate.Checked;
        foreach (var c in new Control[] { _room, _damping, _mix, _preDelay }) c.Enabled = _reverbOn.Checked;

        _howl.Visible = monitor;
        bool speakers = IsSpeakers(S.MonitorDeviceId);
        _howl.Text = speakers
            ? L.T("⚠ 所选输出设备是扬声器：监听会产生啸叫，请改用耳机。")
            : L.T("⚠ 用扬声器监听会产生啸叫，建议戴耳机。");
        _howl.ForeColor = speakers ? Icons.Error : Warning;
    }

    /// <summary>Windows reports the output as loudspeakers (not headphones/headset/unknown).</summary>
    private bool IsSpeakers(string? deviceId)
    {
        var device = deviceId == null ? _defaultOutput : _outputs.FirstOrDefault(d => d.Id == deviceId);
        return device is { FormFactor: EndpointFormFactor.Speakers };
    }

    private bool MonitorsSpeakers(bool micOn) =>
        micOn && S.Destination != MicDestination.HomePod && IsSpeakers(S.MonitorDeviceId);

    private bool ConfirmSpeakers() => MessageBox.Show(this,
        L.T("监听设备是扬声器：麦克风会再次收到扬声器的声音，可能产生很响的啸叫。\n\n建议戴耳机，或只把人声送到 HomePod。仍要开启本机监听吗？"),
        Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    private void UpdateStatus()
    {
        var mic = _fx.Mic;
        _meter.SetLevel(mic?.Running == true ? mic.Peak : 0);
        _micStatus.Text = mic == null ? L.T("麦克风已关闭。")
            : mic.Error is { } error ? L.F("麦克风打不开：{0}", error)
            : !mic.Running ? L.T("正在打开麦克风…")
            : L.F("{0} · {1} Hz · 周期 {2:F1} ms", mic.DeviceName, mic.DeviceRate, mic.PeriodMs) +
              (mic.LowLatency ? L.T("（低延迟模式）") : "");
        _micStatus.ForeColor = mic?.Error != null ? Icons.Error : Color.DimGray;

        var lines = new List<string>();
        if (mic != null && S.Destination != MicDestination.Monitor)
            lines.Add(_fx.HomePodSource.IsBeingRead ? L.T("HomePod：人声正在推送。") : L.T("HomePod：未在推送（连接音箱后人声才会送出）。"));
        if (_fx.Monitor is { } monitor)
        {
            lines.Add(monitor.Error is { } monitorError ? L.F("本机监听打不开：{0}", monitorError)
                : !monitor.Running ? L.T("正在打开监听设备…")
                : L.F("本机监听：{0} · 延迟约 {1:F0} ms", monitor.DeviceName, monitor.EstimatedLatencyMs) +
                  (monitor.Underruns > 0 ? L.F(" · 断音 {0} 次", monitor.Underruns) : ""));
        }
        _routeStatus.Text = string.Join("\n", lines);
        _routeStatus.Visible = lines.Count > 0;
    }

    // ---------------------------------------------------------------- lifetime

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _statusTimer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _statusTimer.Stop();
        if (_saveDebounce.Enabled)
        {
            _saveDebounce.Stop();
            _config.Save();
        }
        if (_ownsEngine) _fx.Dispose();
        base.OnFormClosed(e);
    }

    // ---------------------------------------------------------------- parts

    /// <summary>Preset picker plus one vertical slider per band (−12…+12 dB).</summary>
    private sealed class EqEditor
    {
        private const int Range = 12;
        private readonly ComboBox _preset = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
        private readonly TrackBar[] _bands = new TrackBar[Equalizer.BandCount];
        private readonly Label[] _values = new Label[Equalizer.BandCount];
        private EqSettings _settings = new();
        private bool _loading;

        public GroupBox Box { get; }
        public event Action? Changed;

        public EqEditor(string title)
        {
            foreach (var p in EqPresets.All) _preset.Items.Add(EqPresets.Name(p));
            var t = Table(Equalizer.BandCount);
            var presetRow = Flow(Caption(L.T("预设")), _preset);
            presetRow.Margin = new Padding(0, 0, 0, 6);
            t.Controls.Add(presetRow, 0, 0);
            t.SetColumnSpan(presetRow, Equalizer.BandCount);
            for (int i = 0; i < Equalizer.BandCount; i++)
            {
                _values[i] = new Label { AutoSize = false, Size = new Size(48, 18), TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.DimGray };
                // Vertical: minimum at the bottom, so up = boost.
                _bands[i] = new TrackBar
                {
                    Orientation = Orientation.Vertical,
                    Minimum = -Range,
                    Maximum = Range,
                    TickStyle = TickStyle.Both,
                    TickFrequency = 6,
                    AutoSize = false,
                    Size = new Size(48, 120),
                    SmallChange = 1,
                    LargeChange = 3,
                };
                var label = new Label { Text = Equalizer.BandLabels[i], AutoSize = false, Size = new Size(48, 18), TextAlign = ContentAlignment.MiddleCenter };
                t.Controls.Add(_values[i], i, 1);
                t.Controls.Add(_bands[i], i, 2);
                t.Controls.Add(label, i, 3);
                _bands[i].ValueChanged += (_, _) => OnBandChanged();
            }
            _preset.SelectedIndexChanged += (_, _) => OnPresetChanged();
            Box = Group(title, t);
        }

        public void Load(EqSettings settings)
        {
            _settings = settings;
            if (_settings.Gains is not { Length: Equalizer.BandCount }) _settings.Gains = new double[Equalizer.BandCount];
            _loading = true;
            var preset = EqPresets.FromId(settings.Preset);
            _preset.SelectedIndex = IndexOf(preset);
            ShowGains(EqPresets.Gains(preset) ?? settings.Gains);
            _loading = false;
        }

        private static int IndexOf(EqPreset p) => Math.Max(0, EqPresets.All.ToList().IndexOf(p));

        private void ShowGains(double[] gains)
        {
            bool was = _loading;
            _loading = true;
            for (int i = 0; i < Equalizer.BandCount; i++)
            {
                int g = Math.Clamp((int)Math.Round(gains[i]), -Range, Range);
                _bands[i].Value = g;
                _values[i].Text = g == 0 ? "0" : $"{g:+0;-0}";
            }
            _loading = was;
        }

        private void OnPresetChanged()
        {
            if (_loading) return;
            var preset = EqPresets.All[_preset.SelectedIndex];
            _settings.Preset = EqPresets.Id(preset);
            ShowGains(EqPresets.Gains(preset) ?? _settings.Gains);
            Changed?.Invoke();
        }

        private void OnBandChanged()
        {
            for (int i = 0; i < Equalizer.BandCount; i++)
            {
                int g = _bands[i].Value;
                _values[i].Text = g == 0 ? "0" : $"{g:+0;-0}";
            }
            if (_loading) return;
            // Moving a band turns any preset into a custom curve starting from it.
            _settings.Gains = _bands.Select(b => (double)b.Value).ToArray();
            _settings.Preset = EqPresets.Id(EqPreset.Custom);
            _loading = true;
            _preset.SelectedIndex = IndexOf(EqPreset.Custom);
            _loading = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Horizontal peak meter: fast attack, slow release, perceptual scale.</summary>
    private sealed class LevelMeter : Control
    {
        private float _shown;

        public LevelMeter()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        public void SetLevel(float peak)
        {
            float level = MathF.Sqrt(Math.Clamp(peak, 0f, 1f));
            _shown = level > _shown ? level : _shown * 0.85f + level * 0.15f;
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
