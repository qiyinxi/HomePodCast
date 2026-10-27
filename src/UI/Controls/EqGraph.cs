using System.Drawing.Drawing2D;
using HomePodCast.Audio;

namespace HomePodCast.UI.Controls;

/// <summary>Names and value texts of EQ bands, shared by the graph, the band fields and screen readers.</summary>
internal static class EqText
{
    public static IReadOnlyList<EqFilterType> Types { get; } =
        [EqFilterType.Peak, EqFilterType.LowShelf, EqFilterType.HighShelf, EqFilterType.LowCut, EqFilterType.HighCut, EqFilterType.Notch];

    public static string TypeName(EqFilterType type) => type switch
    {
        EqFilterType.LowShelf => L.T("低架"),
        EqFilterType.HighShelf => L.T("高架"),
        EqFilterType.LowCut => L.T("低切"),
        EqFilterType.HighCut => L.T("高切"),
        EqFilterType.Notch => L.T("陷波"),
        _ => L.T("峰值"),
    };

    public static string Hz(double f) => f >= 10000 ? $"{f / 1000:0.0} kHz" : f >= 1000 ? $"{f / 1000:0.00} kHz" : $"{f:0} Hz";

    public static string Db(double db) => $"{db:+0.0;-0.0;0.0} dB";

    public static string Q(double q) => q.ToString("0.00");

    public static string Slope(int slope) => $"{slope} dB/oct";

    /// <summary>"峰值 · 1.00 kHz · +3.0 dB · Q 1.00" (what applies to the type; "已关闭" when off).</summary>
    public static string Describe(EqBand band)
    {
        var parts = new List<string> { TypeName(band.Type), Hz(band.Frequency) };
        if (band.HasGain) parts.Add(Db(band.GainDb));
        if (band.HasQ) parts.Add("Q " + Q(band.Q));
        if (band.IsCut) parts.Add(Slope(band.Slope));
        if (!band.Enabled) parts.Add(L.T("已关闭"));
        return string.Join(" · ", parts);
    }
}

/// <summary>
/// Parametric EQ graph: the frequency response (20 Hz–20 kHz on a log axis, ±12 or ±24 dB) with one numbered node per
/// band. Drag a node for frequency and gain (Shift: fine), the wheel changes Q while the graph has the focus, a
/// double-click on a node returns its gain to 0 dB and on empty space adds a band there. Keys: ←/→ frequency
/// (1/12 octave, Shift 1/48), ↑/↓ gain (0.5 dB, Shift 0.1), PageUp/PageDown Q, Home 0 dB, 1–9 and 0 or Ctrl+←/→ choose
/// a band, Space switches it on or off, Insert adds and Delete removes one. <see cref="BandsEdited"/> fires for the
/// user's changes only. Screen readers see one child per band.
/// </summary>
internal sealed class EqGraph : FluentControl
{
    private const double MinHz = Equalizer.MinFrequency, MaxHz = Equalizer.MaxFrequency;
    private static readonly double Decades = Math.Log10(MaxHz / MinHz);

    private readonly List<EqBand> _bands = [];
    private double _outputGainDb;
    private bool _active = true;
    private int _selected = -1, _hot = -1, _drag = -1;
    private Point _dragFrom;
    private EqBand _dragBand;
    private double _range = 12;
    private int _wheelDelta;

    public EqGraph()
    {
        SetStyle(ControlStyles.Selectable | ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true);
        TabStop = true;
        AccessibleRole = AccessibleRole.Diagram;
        AccessibleName = L.T("频率响应");
    }

    /// <summary>Height in 96-DPI units.</summary>
    public int PreferredHeight { get; set; } = 200;

    public IReadOnlyList<EqBand> Bands => _bands;

    /// <summary>The selected band (−1: none).</summary>
    public int SelectedIndex
    {
        get => _selected;
        set => Select(value, user: false);
    }

    /// <summary>The user changed, added or removed a band (mouse or keyboard).</summary>
    public event Action? BandsEdited;

    /// <summary>The selected band changed (also when set in code).</summary>
    public event Action? SelectionChanged;

    /// <summary>Show these bands (no <see cref="BandsEdited"/>); <paramref name="active"/> false draws the curve as bypassed.</summary>
    public void SetBands(IReadOnlyList<EqBand> bands, double outputGainDb, bool active)
    {
        _bands.Clear();
        _bands.AddRange(bands);
        _outputGainDb = outputGainDb;
        _active = active;
        if (_selected >= _bands.Count) Select(_bands.Count - 1, user: false);
        else if (_selected < 0 && _bands.Count > 0) Select(0, user: false);
        if (_drag < 0) _range = RangeFor();
        Invalidate();
        AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
    }

    private void Select(int index, bool user)
    {
        index = Math.Clamp(index, -1, _bands.Count - 1);
        if (index == _selected) return;
        _selected = index;
        Invalidate();
        SelectionChanged?.Invoke();
        if (index >= 0) AccessibilityNotifyClients(AccessibleEvents.Selection, index);
        if (user) AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
    }

    private void Edit(int index, EqBand band)
    {
        _bands[index] = band.Normalized();
        Invalidate();
        BandsEdited?.Invoke();
        AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
    }

    /// <summary>Add a peak band (at most <see cref="Equalizer.MaxBands"/>) and select it; false when full.</summary>
    internal bool AddBand(double frequency, double gainDb)
    {
        if (_bands.Count >= Equalizer.MaxBands) return false;
        _bands.Add(new EqBand(EqFilterType.Peak, frequency, Math.Round(gainDb * 2) / 2, 1).Normalized());
        _selected = -1;
        Select(_bands.Count - 1, user: true);
        BandsEdited?.Invoke();
        return true;
    }

    /// <summary>A new band's frequency: the middle (in octaves) of the widest gap between the bands.</summary>
    internal double FreeFrequency()
    {
        var points = _bands.Select(b => Math.Log10(b.Frequency)).Append(Math.Log10(MinHz)).Append(Math.Log10(MaxHz)).Order().ToArray();
        int widest = 0;
        for (int i = 1; i < points.Length - 1; i++) if (points[i + 1] - points[i] > points[widest + 1] - points[widest]) widest = i;
        return Math.Round(Math.Pow(10, (points[widest] + points[widest + 1]) / 2));
    }

    internal bool RemoveSelected()
    {
        if (_selected < 0) return false;
        _bands.RemoveAt(_selected);
        int next = Math.Min(_selected, _bands.Count - 1);
        _selected = -2; // force a selection change event
        Select(next, user: true);
        BandsEdited?.Invoke();
        return true;
    }

    /// <summary>±12 dB unless a band or the curve needs more; only changed when nothing is being dragged.</summary>
    private double RangeFor()
    {
        double peak = _bands.Where(b => b.Enabled && b.HasGain).Select(b => Math.Abs(b.GainDb)).DefaultIfEmpty(0).Max();
        for (double f = MinHz; f <= MaxHz && peak <= 11.5; f *= 1.12) peak = Math.Max(peak, Math.Abs(Response(f)));
        return peak > 11.5 ? 24 : 12;
    }

    private double Response(double f) => Math.Clamp(Equalizer.ResponseDb(_bands, f) + _outputGainDb, -200, 200);

    // ---------------------------------------------------------------- geometry

    public override Size GetPreferredSize(Size proposedSize) => new(Math.Max(proposedSize.Width, Dp(240)), Dp(PreferredHeight));

    internal Rectangle Plot => new(Dp(34), Dp(8), Math.Max(1, Width - Dp(34 + 10)), Math.Max(1, Height - Dp(8 + 22)));

    private float X(double f)
    {
        var p = Plot;
        return p.Left + (float)(Math.Log10(Math.Clamp(f, MinHz, MaxHz) / MinHz) / Decades * p.Width);
    }

    private double FrequencyAt(float x)
    {
        var p = Plot;
        return MinHz * Math.Pow(10, Math.Clamp((x - p.Left) / p.Width, 0, 1) * Decades);
    }

    private float Y(double db)
    {
        var p = Plot;
        return p.Top + p.Height / 2f - (float)(Math.Clamp(db, -_range * 1.1, _range * 1.1) / _range * p.Height / 2);
    }

    private double GainAt(float y)
    {
        var p = Plot;
        return (p.Top + p.Height / 2f - y) / (p.Height / 2f) * _range;
    }

    /// <summary>Where band <paramref name="i"/>'s node is drawn: at its gain, or on the 0 dB line for cuts and notches.</summary>
    internal PointF NodeCenter(int i)
    {
        var b = _bands[i];
        return new PointF(X(b.Frequency), Y(b.HasGain ? b.GainDb : 0));
    }

    private float NodeRadius => Dp(9);

    /// <summary>The band whose node is under <paramref name="p"/> (the selected one first), or −1.</summary>
    internal int HitTest(Point p)
    {
        float r = NodeRadius + Dp(3);
        bool Hit(int i)
        {
            var c = NodeCenter(i);
            return (c.X - p.X) * (c.X - p.X) + (c.Y - p.Y) * (c.Y - p.Y) <= r * r;
        }
        if (_selected >= 0 && Hit(_selected)) return _selected;
        for (int i = _bands.Count - 1; i >= 0; i--) if (Hit(i)) return i;
        return -1;
    }

    // ---------------------------------------------------------------- mouse

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !Enabled) return;
        int hit = HitTest(e.Location);
        if (hit < 0) return;
        Select(hit, user: true);
        _drag = hit;
        _dragFrom = e.Location;
        _dragBand = _bands[hit];
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag >= 0 && _drag < _bands.Count)
        {
            // Shift: a quarter of the movement, for fine adjustments.
            float k = (ModifierKeys & Keys.Shift) != 0 ? 0.25f : 1f;
            var b = _dragBand;
            double f = FrequencyAt(X(b.Frequency) + (e.X - _dragFrom.X) * k);
            f = Math.Round(f, f < 100 ? 1 : 0);
            var moved = b with { Frequency = f };
            if (b.HasGain) moved = moved with { GainDb = Math.Round(Math.Clamp(GainAt(Y(b.GainDb) + (e.Y - _dragFrom.Y) * k), -Equalizer.MaxGainDb, Equalizer.MaxGainDb), 1) };
            if (moved != _bands[_drag]) Edit(_drag, moved);
            return;
        }
        int hot = HitTest(e.Location);
        if (hot != _hot)
        {
            _hot = hot;
            Cursor = hot >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag < 0) return;
        _drag = -1;
        Capture = false;
        _range = RangeFor();
        Invalidate();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (_drag >= 0 && !Capture)
        {
            _drag = -1;
            _range = RangeFor();
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hot < 0) return;
        _hot = -1;
        Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left || !Enabled) return;
        int hit = HitTest(e.Location);
        if (hit >= 0)
        {
            if (_bands[hit].HasGain && _bands[hit].GainDb != 0) Edit(hit, _bands[hit] with { GainDb = 0 });
        }
        else if (Plot.Contains(e.Location))
        {
            AddBand(Math.Round(FrequencyAt(e.X)), Math.Clamp(GainAt(e.Y), -Equalizer.MaxGainDb, Equalizer.MaxGainDb));
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!Enabled || !Focused || _selected < 0 || !_bands[_selected].HasQ) return; // the page scrolls past
        _wheelDelta += e.Delta;
        int notches = _wheelDelta / 120;
        if (notches == 0) return;
        _wheelDelta -= notches * 120;
        Edit(_selected, _bands[_selected] with { Q = Math.Round(_bands[_selected].Q * Math.Pow(2, notches / 6.0), 2) });
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
    }

    // ---------------------------------------------------------------- keyboard

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home
            or Keys.Space or Keys.Delete or Keys.Insert || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && HandleKey(e.KeyData))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    /// <summary>Keyboard rules; returns whether the key belongs to the graph.</summary>
    internal bool HandleKey(Keys keyData)
    {
        if (!Enabled) return false;
        var key = keyData & Keys.KeyCode;
        bool shift = (keyData & Keys.Shift) != 0, ctrl = (keyData & Keys.Control) != 0;
        if (key is >= Keys.D0 and <= Keys.D9 && !ctrl)
        {
            int n = key == Keys.D0 ? 9 : key - Keys.D1;
            if (n < _bands.Count) Select(n, user: true);
            return true;
        }
        if (key == Keys.Insert) return AddBand(FreeFrequency(), 0) || true;
        if (_selected < 0) return false;
        if (ctrl && key is Keys.Left or Keys.Right)
        {
            if (_bands.Count > 0) Select((_selected + (key == Keys.Right ? 1 : _bands.Count - 1)) % _bands.Count, user: true);
            return true;
        }
        var b = _bands[_selected];
        EqBand? next = key switch
        {
            Keys.Left or Keys.Right => b with { Frequency = Math.Round(b.Frequency * Math.Pow(2, (key == Keys.Right ? 1 : -1) / (shift ? 48.0 : 12.0)), 1) },
            Keys.Up or Keys.Down when b.HasGain => b with { GainDb = Math.Round(b.GainDb + (key == Keys.Up ? 1 : -1) * (shift ? 0.1 : 0.5), 1) },
            Keys.PageUp or Keys.PageDown when b.HasQ => b with { Q = Math.Round(b.Q * Math.Pow(2, (key == Keys.PageUp ? 1 : -1) / 6.0), 2) },
            Keys.Home when b.HasGain => b with { GainDb = 0 },
            Keys.Space => b with { Enabled = !b.Enabled },
            _ => null,
        };
        if (key == Keys.Delete) return RemoveSelected();
        if (next is not { } changed) return key is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home; // not for this type
        if (changed.Normalized() != b) Edit(_selected, changed);
        return true;
    }

    // ---------------------------------------------------------------- painting

    private static readonly double[] GridHz = [20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000];

    private static string HzLabel(double f) => f >= 1000 ? $"{f / 1000:0}k" : $"{f:0}";

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var p = Plot;
        var font = Theme.Font(TextStyle.Caption, DeviceDpi);
        Shapes.FillRound(g, P.Window, p, Dp(4));
        using (var grid = new Pen(P.Divider, Hairline))
        using (var zero = new Pen(P.CardBorder, Math.Max(1, Dp(1.5f))))
        {
            int lastRight = int.MinValue;
            foreach (double f in GridHz)
            {
                float x = X(f);
                if (f > MinHz && f < MaxHz) g.DrawLine(grid, x, p.Top, x, p.Bottom);
                string label = HzLabel(f);
                int w = TextMeasure.Size(label, font, Size.Empty, TextFlags.Measure).Width;
                int left = (int)Math.Clamp(x - w / 2f, p.Left - Dp(4), Width - w);
                if (left < lastRight + Dp(6)) continue; // narrow: skip labels that would touch
                TextRenderer.DrawText(g, label, font, new Rectangle(left, p.Bottom + Dp(3), w, Dp(16)), P.TextSecondary, TextFlags.Line);
                lastRight = left + w;
            }
            for (double db = -_range; db <= _range; db += _range / 2)
            {
                float y = Y(db);
                g.DrawLine(db == 0 ? zero : grid, p.Left, y, p.Right, y);
                string label = db == 0 ? "0" : $"{db:+0;-0}";
                TextRenderer.DrawText(g, label, font, new Rectangle(0, (int)y - Dp(8), p.Left - Dp(6), Dp(16)), P.TextSecondary,
                    TextFlags.Line | TextFormatFlags.Right);
            }
        }

        var state = g.Save();
        g.SetClip(p);
        var accent = _active && Enabled ? P.Accent : P.TextDisabled;
        if (_selected >= 0 && _bands[_selected].Enabled)
        {
            var band = _bands[_selected];
            using var area = CurvePath(f => Equalizer.ResponseDb(band, f), closeToZero: true);
            using var fill = new SolidBrush(Color.FromArgb(48, accent));
            g.FillPath(fill, area);
        }
        using (var area = CurvePath(Response, closeToZero: true))
        using (var fill = new SolidBrush(Color.FromArgb(28, accent)))
            g.FillPath(fill, area);
        using (var curve = CurvePath(Response, closeToZero: false))
        using (var pen = new Pen(accent, Math.Max(1.5f, Dp(2))) { LineJoin = LineJoin.Round })
            g.DrawPath(pen, curve);
        g.Restore(state);

        for (int i = 0; i < _bands.Count; i++) if (i != _selected) DrawNode(g, i, font);
        if (_selected >= 0) DrawNode(g, _selected, font);
        if (FocusVisible) DrawFocusRing(g, new Rectangle(0, 0, Width, Height), Dp(6));
    }

    private GraphicsPath CurvePath(Func<double, double> response, bool closeToZero)
    {
        var p = Plot;
        int n = Math.Max(16, p.Width / 2);
        var points = new PointF[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float x = p.Left + p.Width * i / (float)n;
            points[i] = new PointF(x, Y(response(FrequencyAt(x))));
        }
        var path = new GraphicsPath();
        path.AddLines(points);
        if (closeToZero)
        {
            path.AddLine(p.Right, Y(0), p.Left, Y(0));
            path.CloseFigure();
        }
        return path;
    }

    private void DrawNode(Graphics g, int i, Font font)
    {
        var b = _bands[i];
        var c = NodeCenter(i);
        float r = NodeRadius + (i == _hot || i == _drag ? Dp(1.5f) : 0);
        var rect = new RectangleF(c.X - r, c.Y - r, 2 * r, 2 * r);
        bool selected = i == _selected, on = b.Enabled && _active && Enabled;
        var ring = on ? P.Accent : P.TextDisabled;
        using (var fill = new SolidBrush(selected && on ? P.Accent : P.Card)) g.FillEllipse(fill, rect);
        using (var pen = new Pen(ring, Math.Max(1, Dp(selected ? 2 : 1.5f)))) g.DrawEllipse(pen, rect);
        var text = selected && on ? P.TextOnAccent : on ? P.Text : P.TextDisabled;
        Shapes.Glyph(g, (i + 1).ToString(), font, Rectangle.Round(rect), text);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    // ---------------------------------------------------------------- accessibility

    protected override AccessibleObject CreateAccessibilityInstance() => new GraphAccessible(this);

    private sealed class GraphAccessible(EqGraph owner) : FluentAccessible(owner)
    {
        public override string? Value
        {
            get => owner._selected >= 0 ? L.F("频段 {0}：{1}", owner._selected + 1, EqText.Describe(owner._bands[owner._selected])) : null;
            set { }
        }

        public override int GetChildCount() => owner._bands.Count;
        public override AccessibleObject? GetChild(int index) => index >= 0 && index < owner._bands.Count ? new BandAccessible(owner, index) : null;
    }

    private sealed class BandAccessible(EqGraph owner, int index) : AccessibleObject
    {
        public override string? Name { get => L.F("频段 {0}", index + 1); set { } }
        public override string? Value { get => index < owner._bands.Count ? EqText.Describe(owner._bands[index]) : null; set { } }
        public override AccessibleRole Role => AccessibleRole.ListItem;
        public override AccessibleObject? Parent => owner.AccessibilityObject;
        public override AccessibleStates State => AccessibleStates.Selectable | AccessibleStates.Focusable |
            (index == owner._selected ? AccessibleStates.Selected : AccessibleStates.None);
        public override Rectangle Bounds
        {
            get
            {
                if (index >= owner._bands.Count || !owner.IsHandleCreated) return Rectangle.Empty;
                var c = owner.NodeCenter(index);
                int r = (int)owner.NodeRadius;
                return owner.RectangleToScreen(new Rectangle((int)c.X - r, (int)c.Y - r, 2 * r, 2 * r));
            }
        }
        public override string DefaultAction => L.T("选择");
        public override void DoDefaultAction() => owner.Select(index, user: true);
        public override void Select(AccessibleSelection flags)
        {
            if ((flags & (AccessibleSelection.TakeSelection | AccessibleSelection.AddSelection)) != 0) owner.Select(index, user: true);
        }
    }
}
