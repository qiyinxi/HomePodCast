using HomePodCast.Audio;
using HomePodCast.UI.Controls;

namespace HomePodCast.UI.Pages;

/// <summary>
/// 麦克风与音效: the microphone (device, on/off, gain, noise gate), where the voice goes (HomePod / local
/// monitor / both), reverb, and two equalizers: one on the mic, one on everything sent to the HomePod.
/// Edits AppConfig.Effects and applies every change to the shared <see cref="MicEffects"/> immediately.
/// </summary>
internal sealed class EffectsPage : ScrollPage
{
    private static readonly MicDestination[] Destinations = [MicDestination.HomePod, MicDestination.Monitor, MicDestination.Both];

    private readonly AppConfig _config;
    private readonly MicEffects _fx;
    private readonly bool _ownsEngine;
    private readonly ToolTip _tips = new();

    private readonly ToggleSwitch _micOn = new(L.T("开启麦克风"));
    private readonly LevelMeter _meter = new();
    private readonly FluentComboBox _micDevice = new();
    private readonly FluentButton _refresh;
    private readonly FluentSlider _gain = new() { Minimum = -12, Maximum = 30, SmallChange = 1, LargeChange = 4, FillOrigin = 0 };
    private readonly TextBlock _gainValue = new("");
    private readonly ToggleSwitch _gate = new() { AccessibleName = L.T("噪声门") };
    private readonly FluentSlider _gateThreshold = Ui.Slider(-80, -20, 1, 6);
    private readonly TextBlock _gateValue = new("");
    private readonly Segmented _destination = new(["HomePod", L.T("本机监听"), L.T("两者")]) { EqualWidths = false };
    private readonly FluentComboBox _monitorDevice = new();
    private readonly TextBlock _howl = new("", TextStyle.BodyStrong, TextRole.Caution, wrap: true);
    private readonly TextBlock _delayHint = Ui.Note();
    private readonly TextBlock _micStatus = Ui.Note();
    private readonly TextBlock _routeStatus = Ui.Note();

    private readonly ToggleSwitch _reverbOn = new(L.T("开启混响"));
    private readonly FluentSlider _room = Ui.Slider(0, 100), _damping = Ui.Slider(0, 100), _mix = Ui.Slider(0, 100), _preDelay = Ui.Slider(0, 200);
    private readonly TextBlock _roomValue = new(""), _dampingValue = new(""), _mixValue = new(""), _preDelayValue = new("");

    private readonly EqEditor _micEq, _outputEq;
    private readonly TextBlock _nightNote = Ui.Note();
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 100 };
    private readonly System.Windows.Forms.Timer _saveDebounce = new() { Interval = 600 };
    private List<AudioEndpoint> _inputs = [], _outputs = [];
    private AudioEndpoint? _defaultOutput;
    private bool _loading;

    private EffectsSettings S => _config.Effects;

    public EffectsPage(AppConfig config, MicEffects fx, bool ownsEngine = false) : base(L.T("麦克风与音效"))
    {
        _config = config;
        _fx = fx;
        _ownsEngine = ownsEngine;
        _refresh = Ui.IconButton(Glyph.Refresh, L.T("刷新"), L.T("重新读取设备列表"), _tips);
        _micEq = new EqEditor(L.T("麦克风均衡"));
        _outputEq = new EqEditor(L.T("HomePod 输出均衡（所有声音）"));
        _outputEq.Card.Controls.Add(_nightNote);

        Content.Controls.Add(BuildMicCard());
        Content.Controls.Add(BuildReverbCard());
        Content.Controls.Add(new Columns(_micEq.Card, _outputEq.Card) { MinColumnWidth = 300 });

        WireEvents();
        LoadDevices();
        LoadFromSettings();
        _statusTimer.Tick += (_, _) => UpdateStatus();
    }

    // ---------------------------------------------------------------- layout

    private Card BuildMicCard()
    {
        var title = Ui.Header(L.T("麦克风"));
        var head = Ui.Row(12, title, title, _micOn);
        var device = new FieldRow(L.T("设备"), Ui.Row(6, _micDevice, _micDevice, _refresh));
        var level = new FieldRow(L.T("电平"), _meter);
        var gain = new FieldRow(L.T("增益"), _gain, _gainValue);
        var gate = new FieldRow(L.T("噪声门"), Ui.Row(12, _gateThreshold, _gate, _gateThreshold), _gateValue);
        var route = new FieldRow(L.T("送到"), Ui.Row(0, null, _destination));
        var monitor = new FieldRow(L.T("监听设备"), _monitorDevice);
        FieldRow.AlignCaptions(device, level, gain, gate, route, monitor);
        ShowDelayHint();
        var card = Ui.Card(head, device, level, gain, gate, route, monitor, _howl, _delayHint, _micStatus, _routeStatus);
        card.Gap = 8;
        return card;
    }

    private Card BuildReverbCard()
    {
        var title = Ui.Header(L.T("混响"));
        var head = Ui.Row(12, title, title, _reverbOn);
        FieldRow[] rows =
        [
            new(L.T("房间大小"), _room, _roomValue),
            new(L.T("阻尼"), _damping, _dampingValue),
            new(L.T("混响比例"), _mix, _mixValue),
            new(L.T("预延迟"), _preDelay, _preDelayValue),
        ];
        FieldRow.AlignCaptions(rows);
        var card = Ui.Card([head, .. rows]);
        card.Gap = 8;
        return card;
    }

    // ---------------------------------------------------------------- settings

    private void WireEvents()
    {
        _refresh.Click += (_, _) => LoadDevices();
        _micOn.Toggled += (_, _) => SetMicOn(_micOn.Checked);
        _micDevice.SelectionChangeCommitted += (_, _) => Edit(() => S.MicDeviceId = Pick(_micDevice, _inputs, S.MicDeviceId));
        _monitorDevice.SelectionChangeCommitted += (_, _) => Edit(() => S.MonitorDeviceId = Pick(_monitorDevice, _outputs, S.MonitorDeviceId));
        _gain.Scroll += (_, _) => Edit(() => S.MicGainDb = _gain.Value);
        _gate.Toggled += (_, _) => Edit(() => S.GateEnabled = _gate.Checked);
        _gateThreshold.Scroll += (_, _) => Edit(() => S.GateThresholdDb = _gateThreshold.Value);
        _destination.SelectionChangeCommitted += (_, _) =>
            Edit(() => S.Destination = Destinations[Math.Max(0, _destination.SelectedIndex)]);
        _reverbOn.Toggled += (_, _) => Edit(() => S.ReverbEnabled = _reverbOn.Checked);
        _room.Scroll += (_, _) => Edit(() => S.ReverbRoomSize = _room.Value / 100.0);
        _damping.Scroll += (_, _) => Edit(() => S.ReverbDamping = _damping.Value / 100.0);
        _mix.Scroll += (_, _) => Edit(() => S.ReverbMix = _mix.Value / 100.0);
        _preDelay.Scroll += (_, _) => Edit(() => S.ReverbPreDelayMs = _preDelay.Value);
        _micEq.Changed += () => Edit(() => { });
        _outputEq.Changed += () => Edit(() => { });
        _saveDebounce.Tick += (_, _) => Flush();
    }

    /// <summary>
    /// Turn the mic on or off (the switch here, or the tray flyout); asks first when the voice would also go to
    /// loudspeakers. Returns whether it is on now.
    /// </summary>
    public bool SetMicOn(bool on)
    {
        if (on && !_fx.MicOn && MonitorsSpeakers(micOn: true) && !ConfirmSpeakers())
        {
            _micOn.Checked = false;
            return false;
        }
        _fx.MicOn = on; // not saved: the mic is always off when the app starts
        _micOn.Checked = on;
        UpdateEnabled();
        UpdateStatus();
        return on;
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

    /// <summary>Save now if a change is still waiting for the debounce.</summary>
    public void Flush()
    {
        if (!_saveDebounce.Enabled) return;
        _saveDebounce.Stop();
        _config.Save();
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
    private static string? Pick(FluentComboBox box, List<AudioEndpoint> devices, string? current) =>
        box.SelectedIndex <= 0 ? null : box.SelectedIndex - 1 < devices.Count ? devices[box.SelectedIndex - 1].Id : current;

    private static void Fill(FluentComboBox box, string defaultText, List<AudioEndpoint> devices, string? selectedId)
    {
        box.Items.Clear();
        box.Items.Add(defaultText);
        foreach (var d in devices) box.Items.Add(d.Name);
        int index = devices.FindIndex(d => d.Id == selectedId);
        if (index < 0 && selectedId != null)
        {
            box.Items.Add(L.T("（已断开的设备）")); // keep the saved choice; never fall back silently
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
        _gain.Value = (int)Math.Round(s.MicGainDb);
        _gate.Checked = s.GateEnabled;
        _gateThreshold.Value = (int)Math.Round(s.GateThresholdDb);
        _destination.SelectedIndex = Array.IndexOf(Destinations, s.Destination);
        _reverbOn.Checked = s.ReverbEnabled;
        _room.Value = Percent(s.ReverbRoomSize);
        _damping.Value = Percent(s.ReverbDamping);
        _mix.Value = Percent(s.ReverbMix);
        _preDelay.Value = (int)Math.Round(s.ReverbPreDelayMs);
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

        _howl.Collapsed = !monitor;
        bool speakers = IsSpeakers(S.MonitorDeviceId);
        _howl.Text = speakers
            ? L.T("⚠ 所选输出设备是扬声器：监听会产生啸叫，请改用耳机。")
            : L.T("⚠ 用扬声器监听会产生啸叫，建议戴耳机。");
        _howl.Role = speakers ? TextRole.Critical : TextRole.Caution;
    }

    /// <summary>Windows reports the output as loudspeakers (not headphones/headset/unknown).</summary>
    private bool IsSpeakers(string? deviceId)
    {
        var device = deviceId == null ? _defaultOutput : _outputs.FirstOrDefault(d => d.Id == deviceId);
        return device is { FormFactor: EndpointFormFactor.Speakers };
    }

    private bool MonitorsSpeakers(bool micOn) =>
        micOn && S.Destination != MicDestination.HomePod && IsSpeakers(S.MonitorDeviceId);

    private bool ConfirmSpeakers() => MessageBox.Show(FindForm(),
        L.T("监听设备是扬声器：麦克风会再次收到扬声器的声音，可能产生很响的啸叫。\n\n建议戴耳机，或只把人声送到 HomePod。仍要开启本机监听吗？"),
        L.T("麦克风与音效"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    private void UpdateStatus()
    {
        var mic = _fx.Mic;
        _meter.SetLevel(mic?.Running == true ? mic.Peak : 0);
        _micStatus.Text = mic == null ? L.T("麦克风已关闭。")
            : mic.Error is { } error ? L.F("麦克风打不开：{0}", error)
            : !mic.Running ? L.T("正在打开麦克风…")
            : L.F("{0} · {1} Hz · 周期 {2:F1} ms", mic.DeviceName, mic.DeviceRate, mic.PeriodMs) +
              (mic.LowLatency ? L.T("（低延迟模式）") : "");
        _micStatus.Role = mic?.Error != null ? TextRole.Critical : TextRole.Secondary;

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
        _nightNote.Text = _config.NightMode ? L.T("夜间模式开着：现在用的是「减弱低音」，关掉夜间模式后恢复这里的设置。") : "";
    }

    // ---------------------------------------------------------------- lifetime

    /// <summary>The latency can change while the app runs (scenes, slider): refreshed when the page is shown.</summary>
    private void ShowDelayHint() =>
        _delayHint.Text = L.F("经 HomePod 播出的人声会比实际晚约 {0} ms（推流延迟），唱歌请用「本机监听」并戴耳机。",
            Math.Max(_config.LatencyMs, 100) + 20);

    public override void PageShown()
    {
        ShowDelayHint();
        UpdateStatus();
        _statusTimer.Start();
    }

    public override void PageHidden()
    {
        _statusTimer.Stop();
        _meter.SetLevel(0);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _statusTimer.Dispose();
            Flush();
            _saveDebounce.Dispose();
            _tips.Dispose();
            if (_ownsEngine) _fx.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>For <c>gui --effects</c>: this page on its own (no tray, no streaming), with its own engine.</summary>
    public static ApplicationContext Standalone()
    {
        var config = AppConfig.Load();
        config.Effects ??= new EffectsSettings();
        var page = new EffectsPage(config, new MicEffects(config.Effects), ownsEngine: true);
        return new ApplicationContext(new PageWindow(page, L.T("麦克风与音效")));
    }

    // ---------------------------------------------------------------- equalizer

    /// <summary>Preset picker plus one vertical slider per band (−12…+12 dB).</summary>
    private sealed class EqEditor
    {
        private const int Range = 12;
        private readonly FluentComboBox _preset = new() { MinWidth = 160 }; // wider for longer preset names
        private readonly FluentSlider[] _bands = new FluentSlider[Equalizer.BandCount];
        private readonly TextBlock[] _values = new TextBlock[Equalizer.BandCount];
        private EqSettings _settings = new();
        private bool _loading;

        public Card Card { get; }
        public event Action? Changed;

        public EqEditor(string title)
        {
            foreach (var p in EqPresets.All) _preset.Items.Add(EqPresets.Name(p));
            var bands = new EqBands();
            for (int i = 0; i < Equalizer.BandCount; i++)
            {
                _values[i] = new TextBlock("0", TextStyle.Caption, TextRole.Secondary) { Align = HorizontalAlignment.Center };
                _bands[i] = new FluentSlider
                {
                    Orientation = Orientation.Vertical,
                    Minimum = -Range,
                    Maximum = Range,
                    SmallChange = 1,
                    LargeChange = 3,
                    PreferredLength = 132,
                    FillOrigin = 0,
                    AccessibleName = L.F("{0} Hz", Equalizer.BandLabels[i]),
                };
                var label = new TextBlock(Equalizer.BandLabels[i], TextStyle.Caption) { Align = HorizontalAlignment.Center };
                bands.Add(_values[i], _bands[i], label);
                _bands[i].Scroll += (_, _) => OnBandChanged();
            }
            var preset = new FieldRow(L.T("预设"), Ui.Row(0, null, _preset));
            Card = Ui.Card(Ui.Header(title), preset, bands);
            Card.Gap = 10;
            _preset.SelectionChangeCommitted += (_, _) => OnPresetChanged();
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
            for (int i = 0; i < Equalizer.BandCount; i++)
            {
                int g = Math.Clamp((int)Math.Round(gains[i]), -Range, Range);
                _bands[i].Value = g;
                _values[i].Text = g == 0 ? "0" : $"{g:+0;-0}";
            }
        }

        private void OnPresetChanged()
        {
            if (_loading || _preset.SelectedIndex < 0) return;
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
            _preset.SelectedIndex = IndexOf(EqPreset.Custom);
            Changed?.Invoke();
        }
    }

    /// <summary>Band columns (value, vertical slider, frequency) spread across the card.</summary>
    private sealed class EqBands : LayoutPanel
    {
        private readonly List<(TextBlock Value, FluentSlider Slider, TextBlock Label)> _columns = [];

        public void Add(TextBlock value, FluentSlider slider, TextBlock label)
        {
            _columns.Add((value, slider, label));
            Controls.AddRange([value, slider, label]);
        }

        protected override int Arrange(int width, bool apply)
        {
            if (_columns.Count == 0) return 0;
            int colW = width / _columns.Count;
            int valueH = HeightFor(_columns[0].Value, colW), labelH = HeightFor(_columns[0].Label, colW);
            int sliderH = _columns[0].Slider.GetPreferredSize(Size.Empty).Height;
            int sliderW = Dp(32);
            if (apply)
            {
                for (int i = 0; i < _columns.Count; i++)
                {
                    int x = i * colW;
                    var (value, slider, label) = _columns[i];
                    Place(value, x, 0, colW, valueH);
                    Place(slider, x + (colW - sliderW) / 2, valueH + Dp(2), sliderW, sliderH);
                    Place(label, x, valueH + Dp(4) + sliderH, colW, labelH);
                }
            }
            return valueH + Dp(4) + sliderH + labelH;
        }
    }
}
