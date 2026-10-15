using HomePodCast.Audio;

namespace HomePodCast.UI.Controls;

/// <summary>Preset picker plus one vertical slider per band (−12…+12 dB).</summary>
internal sealed class EqEditor
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
        _preset.AccessibleName = L.T("预设");
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
internal sealed class EqBands : LayoutPanel
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
