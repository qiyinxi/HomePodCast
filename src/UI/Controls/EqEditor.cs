using HomePodCast.Audio;

namespace HomePodCast.UI.Controls;

/// <summary>
/// Parametric EQ card (experimental since 1.3): on/off, presets, the response graph with draggable bands, the selected
/// band's type, frequency, gain, Q and slope, and the output gain with a clipping hint. The five-band presets show as
/// their five filters; editing any band turns the curve into 自定义 (the preset's bands as the start). Edits go into the
/// <see cref="EqSettings"/> given to <see cref="Load"/>, then <see cref="Changed"/>.
/// </summary>
internal sealed class EqEditor
{
    private const int FrequencySteps = 1000, QSteps = 1000;
    private static readonly int[] Slopes = [12, 24, 36, 48];

    private readonly ToggleSwitch _on = new(L.T("开启均衡"));
    private readonly FluentComboBox _preset = new() { MinWidth = 160 }; // wider for longer preset names
    private readonly FluentButton _reset = new(L.T("重置"));
    private readonly FluentButton _add = new(L.T("添加频段"), ButtonKind.Secondary, Glyph.Plus);
    private readonly FluentButton _remove = new(L.T("删除频段"), ButtonKind.Secondary, Glyph.Minus);
    private readonly EqGraph _graph = new();
    private readonly Segmented _bandPicker = new();
    private readonly FluentComboBox _type = new();
    private readonly ToggleSwitch _bandOn = new() { AccessibleName = L.T("启用频段") };
    private readonly FluentSlider _frequency = Slider(0, FrequencySteps, 8, 100);
    private readonly FluentSlider _gain = Slider(-240, 240, 5, 30);
    private readonly FluentSlider _q = Slider(0, QSteps, 22, 131);
    private readonly Segmented _slope = new(Slopes.Select(EqText.Slope).ToArray());
    private readonly FluentSlider _output = Slider(-240, 120, 5, 30);
    private readonly TextBlock _frequencyValue = new(""), _gainValue = new(""), _qValue = new(""), _outputValue = new("");
    private readonly FieldRow _gainRow, _qRow, _slopeRow;
    private readonly TextBlock _headroom = new("", TextStyle.Caption, TextRole.Caution, wrap: true);
    private readonly FluentButton _fixHeadroom = new(L.T("降低输出增益"), ButtonKind.Link) { Collapsed = true };
    private EqSettings _settings = new();
    private bool _loading;

    public Card Card { get; }
    public event Action? Changed;

    public EqEditor(string title, int graphHeight = 200)
    {
        foreach (var p in EqPresets.All) _preset.Items.Add(EqPresets.Name(p));
        foreach (var t in EqText.Types) _type.Items.Add(EqText.TypeName(t));
        _graph.PreferredHeight = graphHeight;
        _preset.AccessibleName = L.T("预设");
        _type.AccessibleName = L.T("类型");
        _bandPicker.AccessibleName = L.T("频段");
        _slope.AccessibleName = L.T("斜率");
        _frequency.ValueText = v => EqText.Hz(FrequencyOf(v));
        _gain.ValueText = v => EqText.Db(v / 10.0);
        _q.ValueText = v => EqText.Q(QOf(v));
        _output.ValueText = v => EqText.Db(v / 10.0);
        _gain.FillOrigin = _output.FillOrigin = 0;
        _gain.ResetValue = _output.ResetValue = 0;

        var header = Ui.Header(title);
        var head = Ui.Row(12, header, header, _on);
        var tools = Ui.Row(8, null, _preset, _reset, _add, _remove);
        tools.Wrap = true;
        var preset = new FieldRow(L.T("预设"), tools);
        var band = new FieldRow(L.T("频段"), Ui.Row(12, null, _bandPicker, _bandOn));
        var type = new FieldRow(L.T("类型"), Ui.Row(0, null, _type));
        var frequency = new FieldRow(L.T("频率"), _frequency, _frequencyValue) { ValueWidth = 72 };
        _gainRow = new FieldRow(L.T("增益"), _gain, _gainValue) { ValueWidth = 72 };
        _qRow = new FieldRow(L.T("Q 值"), _q, _qValue) { ValueWidth = 72 };
        _slopeRow = new FieldRow(L.T("斜率"), Ui.Row(0, null, _slope));
        var output = new FieldRow(L.T("输出增益"), _output, _outputValue) { ValueWidth = 72 };
        FieldRow.AlignCaptions(preset, band, type, frequency, _gainRow, _qRow, _slopeRow, output);
        Card = Ui.Card(head, preset, _graph, band, type, frequency, _gainRow, _qRow, _slopeRow, output,
            Ui.Note(L.T("实验性：拖动曲线上的圆点调频率和增益，滚轮或 PageUp/PageDown 调 Q 值，双击空白处添加频段。")), // experimental since 1.3
            _headroom, Ui.Row(0, null, _fixHeadroom));
        Card.Gap = 10;

        _on.Toggled += (_, _) => Edit(s => s.Enabled = _on.Checked, toCustom: false);
        _preset.SelectionChangeCommitted += (_, _) => OnPresetChanged();
        _reset.Click += (_, _) => Edit(s =>
        {
            s.Bands = Equalizer.LegacyBands(new double[Equalizer.BandCount]).Select(EqBandSettings.From).ToList();
            s.OutputGainDb = 0;
        }, toCustom: true, keepBands: false);
        _add.Click += (_, _) => { _graph.AddBand(_graph.FreeFrequency(), 0); };
        _remove.Click += (_, _) => _graph.RemoveSelected();
        _graph.BandsEdited += () => Edit(s => s.Bands = _graph.Bands.Select(EqBandSettings.From).ToList(), toCustom: true, keepBands: false);
        _graph.SelectionChanged += ShowBand;
        _bandPicker.SelectionChangeCommitted += (_, _) => _graph.SelectedIndex = _bandPicker.SelectedIndex;
        _bandOn.Toggled += (_, _) => EditBand(b => b with { Enabled = _bandOn.Checked });
        _type.SelectionChangeCommitted += (_, _) => EditBand(b => WithType(b, EqText.Types[Math.Max(0, _type.SelectedIndex)]));
        // Not rounded: the slider is set again from the value, and a rounded one moved the thumb by a step while dragging.
        _frequency.Scroll += (_, _) => EditBand(b => b with { Frequency = FrequencyOf(_frequency.Value) });
        _gain.Scroll += (_, _) => EditBand(b => b with { GainDb = _gain.Value / 10.0 });
        _q.Scroll += (_, _) => EditBand(b => b with { Q = QOf(_q.Value) });
        _slope.SelectionChangeCommitted += (_, _) => EditBand(b => b with { Slope = Slopes[Math.Max(0, _slope.SelectedIndex)] });
        _output.Scroll += (_, _) => Edit(s => s.OutputGainDb = _output.Value / 10.0, toCustom: false);
        _fixHeadroom.Click += (_, _) => Edit(s => s.OutputGainDb = Math.Clamp(Math.Floor((s.OutputGainDb - PeakDb()) * 10) / 10,
            Equalizer.MinOutputGainDb, Equalizer.MaxOutputGainDb), toCustom: false);
    }

    private static FluentSlider Slider(int min, int max, int small, int large)
    {
        var s = Ui.Slider(min, max, small, large);
        s.WheelSteps = true;
        return s;
    }

    private static double FrequencyOf(int v) => Equalizer.MinFrequency * Math.Pow(Equalizer.MaxFrequency / Equalizer.MinFrequency, v / (double)FrequencySteps);
    private static int StepOf(double f) => (int)Math.Round(Math.Log(f / Equalizer.MinFrequency) / Math.Log(Equalizer.MaxFrequency / Equalizer.MinFrequency) * FrequencySteps);
    private static double QOf(int v) => Equalizer.MinQ * Math.Pow(Equalizer.MaxQ / Equalizer.MinQ, v / (double)QSteps);
    private static int QStepOf(double q) => (int)Math.Round(Math.Log(q / Equalizer.MinQ) / Math.Log(Equalizer.MaxQ / Equalizer.MinQ) * QSteps);

    /// <summary>A band of another type keeps its frequency; cuts start at 12 dB/oct with a flat Q, the others at Q 1.</summary>
    private static EqBand WithType(EqBand b, EqFilterType type)
    {
        if (type == b.Type) return b;
        var next = b with { Type = type };
        if (next.IsCut && !b.IsCut) next = next with { Q = 0.707, Slope = 12 };
        else if (!next.IsCut && b.IsCut) next = next with { Q = 1 };
        return next;
    }

    // ---------------------------------------------------------------- settings

    public void Load(EqSettings settings)
    {
        _settings = settings;
        if (_settings.Gains is not { Length: Equalizer.BandCount }) _settings.Gains = new double[Equalizer.BandCount];
        Show();
    }

    /// <summary>The bands the preset plays: a five-band preset's filters, or the custom bands (or saved five-band values).</summary>
    private IReadOnlyList<EqBand> ShownBands()
    {
        var preset = EqPresets.FromId(_settings.Preset);
        if (preset != EqPreset.Custom) return Equalizer.LegacyBands(EqPresets.Gains(preset)!);
        if (_settings.Bands is { } bands) return bands.Where(b => b != null).Take(Equalizer.MaxBands).Select(b => b.ToBand()).ToList();
        return Equalizer.LegacyBands(_settings.Gains);
    }

    private void Show()
    {
        _loading = true;
        try
        {
            _on.Checked = _settings.Enabled;
            _preset.SelectedIndex = Math.Max(0, EqPresets.All.ToList().IndexOf(EqPresets.FromId(_settings.Preset)));
            _graph.SetBands(ShownBands(), _settings.OutputGainDb, _settings.Enabled);
            _output.Value = (int)Math.Round(_settings.OutputGainDb * 10);
            _outputValue.Text = EqText.Db(_settings.OutputGainDb);
            ShowBand();
            ShowHeadroom();
        }
        finally { _loading = false; }
    }

    private void ShowBand()
    {
        bool was = _loading;
        _loading = true;
        try
        {
            var bands = _graph.Bands;
            string[] numbers = Enumerable.Range(1, bands.Count).Select(i => i.ToString()).ToArray();
            if (!_bandPicker.Items.SequenceEqual(numbers)) _bandPicker.Items = numbers;
            int i = _graph.SelectedIndex;
            _bandPicker.SelectedIndex = i;
            bool has = i >= 0 && i < bands.Count;
            foreach (var c in new FluentControl[] { _bandPicker, _bandOn, _type, _frequency, _slope }) c.Enabled = has;
            _add.Enabled = bands.Count < Equalizer.MaxBands;
            _remove.Enabled = has;
            if (!has)
            {
                _gain.Enabled = _q.Enabled = false;
                _frequencyValue.Text = _gainValue.Text = _qValue.Text = "";
                return;
            }
            var b = bands[i];
            _bandOn.Checked = b.Enabled;
            _type.SelectedIndex = Math.Max(0, EqText.Types.ToList().IndexOf(b.Type));
            _frequency.Value = StepOf(b.Frequency);
            _frequencyValue.Text = EqText.Hz(b.Frequency);
            _gain.Enabled = b.HasGain;
            _gain.Value = (int)Math.Round(b.GainDb * 10);
            _gainValue.Text = b.HasGain ? EqText.Db(b.GainDb) : "";
            _q.Enabled = b.HasQ;
            _q.Value = QStepOf(b.Q);
            _qValue.Text = b.HasQ ? EqText.Q(b.Q) : "";
            _slopeRow.Collapsed = !b.IsCut;
            _slope.SelectedIndex = Math.Max(0, Array.IndexOf(Slopes, b.Slope));
        }
        finally { _loading = was; }
    }

    /// <summary>Largest level the curve adds anywhere (dB; 0 when it only cuts), with the output gain.</summary>
    private double PeakDb()
    {
        if (!_settings.Enabled) return 0;
        var bands = _graph.Bands;
        double peak = 0;
        for (double f = Equalizer.MinFrequency; f <= Equalizer.MaxFrequency; f *= 1.03)
            peak = Math.Max(peak, Equalizer.ResponseDb(bands, f) + _settings.OutputGainDb);
        return peak;
    }

    private void ShowHeadroom()
    {
        double peak = PeakDb();
        bool clips = peak >= 0.5;
        _headroom.Text = clips ? L.F("曲线最高提升 {0}，声音很大时可能削波。", EqText.Db(peak)) : "";
        _fixHeadroom.Collapsed = !clips || _settings.OutputGainDb <= Equalizer.MinOutputGainDb;
    }

    private void OnPresetChanged()
    {
        if (_loading || _preset.SelectedIndex < 0) return;
        _settings.Preset = EqPresets.Id(EqPresets.All[_preset.SelectedIndex]);
        Show();
        Changed?.Invoke();
    }

    /// <summary>
    /// Change the settings and show them. <paramref name="toCustom"/>: the edit makes the curve 自定义; unless the change
    /// sets the bands itself (<paramref name="keepBands"/> false), a five-band preset's filters become the custom bands first.
    /// </summary>
    private void Edit(Action<EqSettings> change, bool toCustom, bool keepBands = true)
    {
        if (_loading) return;
        if (toCustom && EqPresets.FromId(_settings.Preset) != EqPreset.Custom && keepBands)
            _settings.Bands = ShownBands().Select(EqBandSettings.From).ToList();
        change(_settings);
        if (toCustom) _settings.Preset = EqPresets.Id(EqPreset.Custom);
        if (EqPresets.FromId(_settings.Preset) == EqPreset.Custom && _settings.Bands != null)
            _settings.Gains = Equalizer.LegacyValues(_settings.Bands.Select(b => b.ToBand()).ToList(), _settings.OutputGainDb);
        int selected = _graph.SelectedIndex;
        Show();
        _graph.SelectedIndex = Math.Min(selected, _graph.Bands.Count - 1);
        Changed?.Invoke();
    }

    private void EditBand(Func<EqBand, EqBand> change)
    {
        int i = _graph.SelectedIndex;
        if (_loading || i < 0 || i >= _graph.Bands.Count) return;
        var bands = _graph.Bands.ToList();
        bands[i] = change(bands[i]).Normalized();
        Edit(s => s.Bands = bands.Select(EqBandSettings.From).ToList(), toCustom: true, keepBands: false);
    }
}
