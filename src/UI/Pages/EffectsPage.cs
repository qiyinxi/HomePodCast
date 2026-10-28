using HomePodCast.Audio;
using HomePodCast.UI.Controls;

namespace HomePodCast.UI.Pages;

/// <summary>
/// 麦克风: device and input channel, monitoring on/off, input and output levels, gain, noise gate, where the voice goes
/// (HomePod / local monitor / both), then the voice chain in signal order: low cut, mic EQ, compressor, de-esser, reverb,
/// limiter (the new stages experimental since 1.3). Output EQ lives on its own page.
/// Edits AppConfig.Effects and applies every change to the shared <see cref="MicEffects"/> immediately.
/// </summary>
internal sealed class EffectsPage : ScrollPage
{
    private static readonly MicDestination[] Destinations = [MicDestination.HomePod, MicDestination.Monitor, MicDestination.Both];
    private static readonly int[] LowCutSlopes = [12, 24];

    private readonly AppConfig _config;
    private readonly MicEffects _fx;
    private readonly bool _ownsEngine;
    // Captions are painted by their rows (no window of their own): only a LayoutToolTip shows their hints.
    private readonly LayoutToolTip _tips = new();

    private readonly ToggleSwitch _micOn = new(L.T("麦克风监听"));
    private readonly LevelMeter _meter = new();
    private readonly LevelMeter _outputMeter = new();
    private readonly FluentComboBox _micDevice = new();
    private readonly FluentComboBox _micChannel = new();
    private readonly FieldRow _channelRow;
    private readonly FluentButton _refresh;
    private readonly FluentSlider _gain = new() { Minimum = -12, Maximum = 30, SmallChange = 1, LargeChange = 4, FillOrigin = 0 };
    private readonly TextBlock _gainValue = new("");
    private readonly ToggleSwitch _gate = new() { AccessibleName = L.T("噪声门") };
    private readonly FluentSlider _gateThreshold = Ui.Slider(-80, -20, 1, 6);
    private readonly TextBlock _gateValue = new("");
    private readonly Segmented _destination = new(["HomePod", L.T("本机"), L.T("两者")]) { EqualWidths = false };
    private readonly FluentComboBox _monitorDevice = new();
    private readonly TextBlock _howl = new("", TextStyle.BodyStrong, TextRole.Caution, wrap: true);
    private readonly TextBlock _monitorCaptured = new(L.T(LocalMonitor.CapturedText), TextStyle.Body, TextRole.Caution, wrap: true) { Collapsed = true };
    private readonly TextBlock _delayHint = Ui.Note();
    private readonly TextBlock _micStatus = Ui.Note();
    private readonly FluentButton _micPrivacy = new(L.T("打开麦克风隐私设置"), ButtonKind.Link, Glyph.OpenInNew) { Collapsed = true };
    private readonly TextBlock _routeStatus = Ui.Note();

    private readonly ToggleSwitch _lowCutOn = new(L.T("开启低切"));
    private readonly Segmented _lowCutSlope = new(LowCutSlopes.Select(EqText.Slope).ToArray());
    private readonly ToggleSwitch _compressorOn = new(L.T("开启压缩器"));
    private readonly ReductionMeter _compressorMeter = new();
    private readonly ToggleSwitch _deEsserOn = new(L.T("开启齿音消除"));
    private readonly ReductionMeter _deEsserMeter = new();
    private readonly ToggleSwitch _limiterOn = new(L.T("开启限制器"));
    private readonly ReductionMeter _limiterMeter = new() { RangeDb = 12 };

    private readonly ToggleSwitch _reverbOn = new(L.T("开启混响"));
    private readonly FluentSlider _room = Ui.Slider(0, 100), _damping = Ui.Slider(0, 100), _mix = Ui.Slider(0, 100), _preDelay = Ui.Slider(0, 200);
    private readonly TextBlock _roomValue = new(""), _dampingValue = new(""), _mixValue = new(""), _preDelayValue = new("");

    /// <summary>A slider of the voice chain bound to one setting (scaled, maybe logarithmic), with its value text.</summary>
    private sealed record Param(FluentSlider Slider, TextBlock Value, Func<int, double> From, Func<double, int> To,
        Func<double, string> Text, Func<EffectsSettings, double> Get, Action<EffectsSettings, double> Set, ToggleSwitch? Section);

    private readonly List<Param> _params = [];
    private readonly EqEditor _micEq;
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 100 };
    private readonly System.Windows.Forms.Timer _saveDebounce = new() { Interval = 600 };
    private List<AudioEndpoint> _inputs = [], _outputs = [];
    private AudioEndpoint? _defaultOutput;
    private int _channels;
    private bool _loading;

    private EffectsSettings S => _config.Effects;

    public EffectsPage(AppConfig config, MicEffects fx, bool ownsEngine = false) : base(L.T("麦克风"))
    {
        _config = config;
        _fx = fx;
        _ownsEngine = ownsEngine;
        _refresh = Ui.IconButton(Glyph.Refresh, L.T("刷新"), L.T("重新读取设备列表"), _tips);
        _channelRow = new FieldRow(L.T("输入声道"), Ui.Row(0, null, _micChannel)) { Collapsed = true };
        _micEq = new EqEditor(L.T("麦克风均衡"), graphHeight: 170);

        Content.Controls.Add(BuildMicCard());
        Content.Controls.Add(BuildLowCutCard());
        Content.Controls.Add(_micEq.Card);
        Content.Controls.Add(new Columns(BuildCompressorCard(), BuildDeEsserCard()) { EqualHeights = true });
        Content.Controls.Add(BuildReverbCard());
        Content.Controls.Add(BuildLimiterCard());

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
        // A FieldRow names the row of controls after its caption; the control in it gets the caption too (screen readers).
        _micDevice.AccessibleName = L.T("输入设备");
        _micChannel.AccessibleName = L.T("输入声道");
        _gateThreshold.AccessibleName = L.T("噪声门阈值");
        _destination.AccessibleName = L.T("输出到");
        _meter.AccessibleName = L.T("输入电平");
        _outputMeter.AccessibleName = L.T("输出电平");
        _gain.ValueText = v => $"{v:+0;-0;0} dB";
        _gateThreshold.ValueText = v => $"{v} dB";
        var device = new FieldRow(L.T("输入设备"), Ui.Row(6, _micDevice, _micDevice, _refresh));
        var level = new FieldRow(L.T("输入电平"), _meter);
        var output = new FieldRow(L.T("输出电平"), _outputMeter);
        var gain = new FieldRow(L.T("增益"), _gain, _gainValue);
        var gate = new FieldRow(L.T("噪声门"), Ui.Row(12, _gateThreshold, _gate, _gateThreshold), _gateValue);
        var route = new FieldRow(L.T("输出到"), Ui.Row(0, null, _destination));
        var monitor = new FieldRow(L.T("本机输出设备"), _monitorDevice);
        FieldRow.AlignCaptions(device, _channelRow, level, output, gain, gate, route, monitor);
        ShowDelayHint();
        var card = Ui.Card(head, _delayHint, Ui.Note(L.T("每次启动 HomePodCast 时麦克风默认关闭。")),
            device, _channelRow, level, gain, gate, output, route, monitor, _monitorCaptured, _howl, _micStatus,
            Ui.Row(0, null, _micPrivacy), _routeStatus,
            // experimental since 1.3
            Ui.Note(L.T("声音依次经过：增益 → 噪声门 → 低切 → 均衡 → 压缩器 → 齿音消除 → 混响 → 限制器。低切、均衡、压缩器、齿音消除和限制器是实验性功能。")));
        card.Gap = 8;
        return card;
    }

    private static Func<int, double> Log(double low, double high) => v => low * Math.Pow(high / low, v / 1000.0);
    private static Func<double, int> LogStep(double low, double high) => x => (int)Math.Round(Math.Log(Math.Clamp(x, low, high) / low) / Math.Log(high / low) * 1000);

    /// <summary>A row for one setting of the voice chain; <paramref name="section"/> is the switch that turns its stage on.</summary>
    private FieldRow ParamRow(string caption, string hint, int min, int max, int small, int large, Func<int, double> from, Func<double, int> to,
        Func<double, string> text, Func<EffectsSettings, double> get, Action<EffectsSettings, double> set, ToggleSwitch section)
    {
        var slider = Ui.Slider(min, max, small, large);
        slider.WheelSteps = true;
        slider.AccessibleName = caption;
        slider.ValueText = v => text(from(v));
        var value = new TextBlock("");
        var p = new Param(slider, value, from, to, text, get, set, section);
        _params.Add(p);
        slider.Scroll += (_, _) => Edit(() => set(S, from(slider.Value)));
        var row = new FieldRow(caption, slider, value) { ValueWidth = 72 };
        _tips.SetToolTip(row.Caption, hint);
        _tips.SetToolTip(slider, hint);
        slider.AccessibleDescription = hint;
        return row;
    }

    /// <summary>
    /// A stage's card. Its controls are named after the stage too (「压缩器 · 阈值」): the page has two 阈值, two 释放时间 and
    /// three 增益衰减, which a screen reader could not tell apart.
    /// </summary>
    private static Card Section(ToggleSwitch on, string title, params Control[] rows)
    {
        foreach (var row in rows.OfType<FieldRow>())
        {
            var named = row.Field is RowPanel panel ? LogicalLayout.Children(panel).OfType<FluentControl>().FirstOrDefault() : row.Field;
            if (named != null) named.AccessibleName = $"{title} · {row.Caption.Text}";
        }
        var header = Ui.Header(title);
        var card = Ui.Card([Ui.Row(12, header, header, on), .. rows]);
        card.Gap = 8;
        FieldRow.AlignCaptions(rows.OfType<FieldRow>().ToArray());
        return card;
    }

    private Card BuildLowCutCard()
    {
        _lowCutSlope.AccessibleName = L.T("斜率");
        var frequency = ParamRow(L.T("频率"), L.T("低于这个频率的声音被切掉：去掉低频嗡嗡声、碰麦和喷麦。"), 0, 1000, 10, 100,
            Log(20, 400), LogStep(20, 400), EqText.Hz, s => s.LowCutHz, (s, v) => s.LowCutHz = Math.Round(v), _lowCutOn);
        return Section(_lowCutOn, L.T("低切"), frequency, new FieldRow(L.T("斜率"), Ui.Row(0, null, _lowCutSlope)));
    }

    private Card BuildCompressorCard()
    {
        _compressorMeter.AccessibleName = L.T("增益衰减");
        string Db(double v) => $"{v:0.#} dB";
        string Ms(double v) => v < 10 ? $"{v:0.0} ms" : $"{v:0} ms";
        return Section(_compressorOn, L.T("压缩器"),
            ParamRow(L.T("阈值"), L.T("超过这个电平的声音开始被压缩。"), -60, 0, 1, 6, v => v, x => (int)Math.Round(x), Db,
                s => s.CompressorThresholdDb, (s, v) => s.CompressorThresholdDb = v, _compressorOn),
            ParamRow(L.T("比率"), L.T("超过阈值的部分被压缩的程度：4:1 表示超出 4 dB 只剩 1 dB。"), 10, 200, 5, 20, v => v / 10.0, x => (int)Math.Round(x * 10),
                v => $"{v:0.0}:1", s => s.CompressorRatio, (s, v) => s.CompressorRatio = v, _compressorOn),
            ParamRow(L.T("拐点"), L.T("在阈值附近逐渐开始压缩的范围，越大越柔和。"), 0, 24, 1, 6, v => v, x => (int)Math.Round(x), Db,
                s => s.CompressorKneeDb, (s, v) => s.CompressorKneeDb = v, _compressorOn),
            ParamRow(L.T("启动时间"), L.T("声音超过阈值后多快开始压缩。"), 0, 1000, 20, 100, Log(0.1, 200), LogStep(0.1, 200), Ms,
                s => s.CompressorAttackMs, (s, v) => s.CompressorAttackMs = Math.Round(v, 1), _compressorOn),
            ParamRow(L.T("释放时间"), L.T("声音回落后多快停止压缩。"), 0, 1000, 20, 100, Log(5, 3000), LogStep(5, 3000), Ms,
                s => s.CompressorReleaseMs, (s, v) => s.CompressorReleaseMs = Math.Round(v), _compressorOn),
            ParamRow(L.T("补偿增益"), L.T("压缩后整体提升的音量。"), 0, 240, 5, 30, v => v / 10.0, x => (int)Math.Round(x * 10), Db,
                s => s.CompressorMakeupDb, (s, v) => s.CompressorMakeupDb = v, _compressorOn),
            new FieldRow(L.T("增益衰减"), _compressorMeter));
    }

    private Card BuildDeEsserCard()
    {
        _deEsserMeter.AccessibleName = L.T("增益衰减");
        return Section(_deEsserOn, L.T("齿音消除"),
            ParamRow(L.T("频率"), L.T("高于这个频率的齿音（“丝”“嘶”声）被压低。"), 0, 1000, 10, 100, Log(2000, 12000), LogStep(2000, 12000), EqText.Hz,
                s => s.DeEsserFrequencyHz, (s, v) => s.DeEsserFrequencyHz = Math.Round(v / 10) * 10, _deEsserOn),
            ParamRow(L.T("阈值"), L.T("齿音超过这个电平时才压低。"), -60, 0, 1, 6, v => v, x => (int)Math.Round(x), v => $"{v:0} dB",
                s => s.DeEsserThresholdDb, (s, v) => s.DeEsserThresholdDb = v, _deEsserOn),
            ParamRow(L.T("最大衰减"), L.T("齿音最多被压低多少。"), 0, 24, 1, 3, v => v, x => (int)Math.Round(x), v => $"{v:0} dB",
                s => s.DeEsserRangeDb, (s, v) => s.DeEsserRangeDb = v, _deEsserOn),
            new FieldRow(L.T("增益衰减"), _deEsserMeter));
    }

    private Card BuildLimiterCard()
    {
        _limiterMeter.AccessibleName = L.T("增益衰减");
        return Section(_limiterOn, L.T("限制器"),
            ParamRow(L.T("上限"), L.T("人声的峰值不会超过这个电平，防止削波失真。"), -240, 0, 5, 30, v => v / 10.0, x => (int)Math.Round(x * 10), v => $"{v:0.0} dB",
                s => s.LimiterCeilingDb, (s, v) => s.LimiterCeilingDb = v, _limiterOn),
            ParamRow(L.T("释放时间"), L.T("峰值过后多快恢复原来的音量。"), 0, 1000, 20, 100, Log(5, 2000), LogStep(5, 2000), v => $"{v:0} ms",
                s => s.LimiterReleaseMs, (s, v) => s.LimiterReleaseMs = Math.Round(v), _limiterOn),
            new FieldRow(L.T("增益衰减"), _limiterMeter));
    }

    private Card BuildReverbCard()
    {
        var title = Ui.Header(L.T("混响"));
        var head = Ui.Row(12, title, title, _reverbOn);
        FieldRow[] rows =
        [
            new(L.T("衰减（Room Size）"), _room, _roomValue),
            new(L.T("高频阻尼（Damping）"), _damping, _dampingValue),
            new(L.T("干湿比（Wet/Dry）"), _mix, _mixValue),
            new(L.T("预延迟（Pre-delay）"), _preDelay, _preDelayValue),
        ];
        string[] hints =
        [
            L.T("调大后混响尾音更长。"),
            L.T("调大后高频衰减更快，混响更柔和。"),
            L.T("调大后混响更多、原声更少；0% 为纯原声，100% 为纯混响。"),
            L.T("调大后混响更晚出现，原声和混响之间的间隔更长。"),
        ];
        for (int i = 0; i < rows.Length; i++)
        {
            _tips.SetToolTip(rows[i].Caption, hints[i]);
            _tips.SetToolTip(rows[i].Field, hints[i]);
            rows[i].Field.AccessibleDescription = hints[i];
        }
        _room.ValueText = _damping.ValueText = _mix.ValueText = v => $"{v}%";
        _preDelay.ValueText = v => $"{v} ms";
        FieldRow.AlignCaptions(rows);
        var card = Ui.Card([head, .. rows]);
        card.Gap = 8;
        return card;
    }

    // ---------------------------------------------------------------- settings

    private void WireEvents()
    {
        _refresh.Click += (_, _) => LoadDevices();
        _micPrivacy.Click += (_, _) => AppEnvironment.OpenUri(MicCapture.PrivacySettingsUri);
        _micOn.Toggled += (_, _) => SetMicOn(_micOn.Checked);
        _micDevice.SelectionChangeCommitted += (_, _) => Edit(() => S.MicDeviceId = Pick(_micDevice, _inputs, S.MicDeviceId));
        _micChannel.SelectionChangeCommitted += (_, _) =>
            Edit(() => S.MicChannel = _micChannel.SelectedIndex >= _channels ? -1 : Math.Max(0, _micChannel.SelectedIndex));
        _monitorDevice.SelectionChangeCommitted += (_, _) => Edit(() => S.MonitorDeviceId = Pick(_monitorDevice, _outputs, S.MonitorDeviceId));
        _gain.Scroll += (_, _) => Edit(() => S.MicGainDb = _gain.Value);
        _gate.Toggled += (_, _) => Edit(() => S.GateEnabled = _gate.Checked);
        _gateThreshold.Scroll += (_, _) => Edit(() => S.GateThresholdDb = _gateThreshold.Value);
        _destination.SelectionChangeCommitted += (_, _) =>
            Edit(() => S.Destination = Destinations[Math.Max(0, _destination.SelectedIndex)]);
        _lowCutOn.Toggled += (_, _) => Edit(() => S.LowCutEnabled = _lowCutOn.Checked);
        _lowCutSlope.SelectionChangeCommitted += (_, _) => Edit(() => S.LowCutSlope = LowCutSlopes[Math.Max(0, _lowCutSlope.SelectedIndex)]);
        _compressorOn.Toggled += (_, _) => Edit(() => S.CompressorEnabled = _compressorOn.Checked);
        _deEsserOn.Toggled += (_, _) => Edit(() => S.DeEsserEnabled = _deEsserOn.Checked);
        _limiterOn.Toggled += (_, _) => Edit(() => S.LimiterEnabled = _limiterOn.Checked);
        _reverbOn.Toggled += (_, _) => Edit(() => S.ReverbEnabled = _reverbOn.Checked);
        _room.Scroll += (_, _) => Edit(() => S.ReverbRoomSize = _room.Value / 100.0);
        _damping.Scroll += (_, _) => Edit(() => S.ReverbDamping = _damping.Value / 100.0);
        _mix.Scroll += (_, _) => Edit(() => S.ReverbMix = _mix.Value / 100.0);
        _preDelay.Scroll += (_, _) => Edit(() => S.ReverbPreDelayMs = _preDelay.Value);
        _micEq.Changed += () => Edit(() => { });
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
        ShowDelayHint();
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

    /// <summary>
    /// The channel choice for a device with <paramref name="channels"/> inputs: 「声道 1」…, then 「全部声道混合」. Shown
    /// only for multi-channel devices, or while a channel other than the first is saved.
    /// </summary>
    private void ShowChannels(int channels)
    {
        int shown = Math.Max(channels, S.MicChannel + 1);
        // The mic is off at every start, so its channel count is unknown: with 全部声道混合 saved, the list would offer
        // nothing else until the mic was turned on (and the voice sent).
        if (channels == 0 && S.MicChannel < 0) shown = Math.Max(shown, 2);
        if (shown != _channels || _micChannel.Items.Count == 0)
        {
            _channels = shown;
            _micChannel.Items.Clear();
            for (int i = 0; i < shown; i++) _micChannel.Items.Add(L.F("声道 {0}", i + 1));
            _micChannel.Items.Add(L.T("全部声道混合"));
        }
        bool was = _loading;
        _loading = true;
        _micChannel.SelectedIndex = S.MicChannel < 0 ? _channels : Math.Min(S.MicChannel, _channels - 1);
        _loading = was;
        _channelRow.Collapsed = channels <= 1 && S.MicChannel == 0;
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
        _lowCutOn.Checked = s.LowCutEnabled;
        _lowCutSlope.SelectedIndex = Math.Max(0, Array.IndexOf(LowCutSlopes, s.LowCutSlope));
        _compressorOn.Checked = s.CompressorEnabled;
        _deEsserOn.Checked = s.DeEsserEnabled;
        _limiterOn.Checked = s.LimiterEnabled;
        foreach (var p in _params) p.Slider.Value = p.To(p.Get(s));
        _reverbOn.Checked = s.ReverbEnabled;
        _room.Value = Percent(s.ReverbRoomSize);
        _damping.Value = Percent(s.ReverbDamping);
        _mix.Value = Percent(s.ReverbMix);
        _preDelay.Value = (int)Math.Round(s.ReverbPreDelayMs);
        s.MicEq ??= new EqSettings();
        _micEq.Load(s.MicEq);
        _loading = false;
        ShowChannels(_fx.Mic?.DeviceChannels ?? 0);
        ShowValues();
        ShowDelayHint();
        UpdateEnabled();
        UpdateStatus();
    }

    public void ReloadConfiguration()
    {
        _saveDebounce.Stop();
        LoadDevices();
        LoadFromSettings();
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
        foreach (var p in _params) p.Value.Text = p.Text(p.Get(S));
    }

    private void UpdateEnabled()
    {
        bool monitor = S.Destination != MicDestination.HomePod;
        _monitorDevice.Enabled = monitor;
        _gateThreshold.Enabled = _gate.Checked;
        foreach (var c in new Control[] { _room, _damping, _mix, _preDelay }) c.Enabled = _reverbOn.Checked;
        foreach (var p in _params) p.Slider.Enabled = p.Section?.Checked != false;
        _lowCutSlope.Enabled = _lowCutOn.Checked;

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
        L.T("麦克风"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    private void UpdateStatus()
    {
        var mic = _fx.Mic;
        bool running = mic?.Running == true;
        _meter.SetLevel(running ? mic!.Peak : 0);
        _outputMeter.SetLevel(running ? _fx.OutputMeter.Peak : 0);
        _compressorMeter.SetReduction(running ? _fx.Compressor.GainReductionDb : 0);
        _deEsserMeter.SetReduction(running ? _fx.DeEsser.GainReductionDb : 0);
        _limiterMeter.SetReduction(running ? _fx.Limiter.GainReductionDb : 0);
        if (running && mic!.DeviceChannels != _channels) ShowChannels(mic.DeviceChannels);
        _micStatus.Text = mic == null ? L.T("麦克风监听已关闭。")
            : mic.Error is { } error ? L.F("无法打开麦克风：{0}", error)
            : !mic.Running ? L.T("正在打开麦克风…")
            : L.F("{0} · {1} Hz · 周期 {2:F1} ms", mic.DeviceName, mic.DeviceRate, mic.PeriodMs) +
              (mic.LowLatency ? L.T("（低延迟模式）") : "");
        _micStatus.Role = mic?.Error != null ? TextRole.Critical : TextRole.Secondary;
        // Refused by the privacy settings: the way to them, and what to do afterwards.
        bool denied = mic?.AccessDenied == true;
        if (denied) _micStatus.Text = L.T("Windows 的隐私设置不允许 HomePodCast 使用麦克风。允许之后，把麦克风关掉再打开。");
        _micPrivacy.Collapsed = !denied;

        var lines = new List<string>();
        if (mic != null && S.Destination != MicDestination.Monitor)
            lines.Add(_fx.HomePodSource.IsBeingRead ? L.T("HomePod：人声正在推送。") : L.T("HomePod：未在推送（连接音箱后人声才会送出）。"));
        if (_fx.Monitor is { } monitor)
        {
            lines.Add(monitor.Error is { } monitorError ? L.F("无法启动本机监听：{0}", monitorError)
                : !monitor.Running ? L.T("正在打开监听设备…")
                : L.F("本机监听：{0} · 延迟约 {1:F0} ms", monitor.DeviceName, monitor.EstimatedLatencyMs) +
                  (monitor.Underruns > 0 ? L.F(" · 断音 {0} 次", monitor.Underruns) : ""));
        }
        _routeStatus.Text = string.Join("\n", lines);
        // 本机监听 is paused while its device is the one captured for the speaker (MicEffects.MonitorBlocked).
        _monitorCaptured.Collapsed = !(mic != null && _fx.MonitorBlocked);
    }

    // ---------------------------------------------------------------- lifetime

    /// <summary>The latency can change while the app runs (scenes, slider): refreshed when the page is shown.</summary>
    private void ShowDelayHint() =>
        _delayHint.Text = S.Destination switch
        {
            MicDestination.Monitor => L.T("你的声音只在监听设备（耳机）里播放，不会推到 HomePod。"),
            MicDestination.Both => L.F("音箱和耳机里都有你的声音；音箱里的晚约 {0} ms。", Math.Max(_config.LatencyMs, 100) + 20),
            _ => L.F("你的声音会和电脑声音一起从 HomePod 播出，比实际晚约 {0} ms。", Math.Max(_config.LatencyMs, 100) + 20),
        };

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
        _outputMeter.SetLevel(0);
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
        return new ApplicationContext(new PageWindow(page, L.T("麦克风")));
    }
}
