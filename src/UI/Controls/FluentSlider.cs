using System.Drawing.Drawing2D;

namespace HomePodCast.UI.Controls;

/// <summary>
/// Fluent slider (horizontal or vertical): rail, accent fill, thumb with hover/pressed states, focus ring.
/// Only part of the range may be selectable (<see cref="Floor"/>, <see cref="Ceiling"/>): the rest of the
/// rail is drawn but the thumb never goes there. Keys: arrows and +/− = <see cref="SmallChange"/>,
/// PageUp/PageDown = <see cref="LargeChange"/>, Home/End = the selectable ends. Clicking the rail jumps there.
/// <see cref="ValueChanged"/> fires for every change; <see cref="Scroll"/> only for the user's.
/// </summary>
internal sealed class FluentSlider : FluentControl
{
    private int _min, _max = 100, _value, _floor = int.MinValue, _ceiling = int.MaxValue;
    private bool _dragging;

    public FluentSlider()
    {
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        AccessibleRole = AccessibleRole.Slider;
    }

    public Orientation Orientation { get; set; } = Orientation.Horizontal;
    public int SmallChange { get; set; } = 1;
    public int LargeChange { get; set; } = 10;

    /// <summary>Length (rail direction) for <see cref="GetPreferredSize"/>, in 96-DPI units.</summary>
    public int PreferredLength { get; set; } = 160;

    /// <summary>A mark on the rail (e.g. the volume cap); null = none.</summary>
    public int? Mark { get; set; }

    /// <summary>Where the accent fill starts (e.g. 0 dB on an equalizer band); null = <see cref="Minimum"/>.</summary>
    public int? FillOrigin { get; set; }

    /// <summary>Raised for every change of <see cref="Value"/>.</summary>
    public event EventHandler? ValueChanged;

    /// <summary>Raised when the user changed the value (mouse or keyboard), after <see cref="ValueChanged"/>.</summary>
    public event EventHandler? Scroll;

    /// <summary>Raised when the mouse lets go of the thumb.</summary>
    public event EventHandler? Released;

    public bool IsDragging => _dragging;

    public int Minimum
    {
        get => _min;
        set { _min = value; if (_max < _min) _max = _min; SetValueCore(_value, user: false); Invalidate(); }
    }

    public int Maximum
    {
        get => _max;
        set { _max = value; if (_min > _max) _min = _max; SetValueCore(_value, user: false); Invalidate(); }
    }

    /// <summary>Lowest value that can be chosen (defaults to <see cref="Minimum"/>).</summary>
    public int Floor
    {
        get => Math.Clamp(_floor, _min, _max);
        set { _floor = value; SetValueCore(_value, user: false); Invalidate(); }
    }

    /// <summary>Highest value that can be chosen (defaults to <see cref="Maximum"/>).</summary>
    public int Ceiling
    {
        get => Math.Clamp(_ceiling, Floor, _max);
        set { _ceiling = value; SetValueCore(_value, user: false); Invalidate(); }
    }

    /// <summary>Programmatic value (clamped to the selectable range); raises ValueChanged but not Scroll.</summary>
    public int Value
    {
        get => _value;
        set => SetValueCore(value, user: false);
    }

    public int Clamp(int value) => Math.Clamp(value, Floor, Ceiling);

    private void SetValueCore(int value, bool user)
    {
        value = Clamp(value);
        if (value == _value) return;
        _value = value;
        Invalidate();
        ValueChanged?.Invoke(this, EventArgs.Empty);
        if (user) Scroll?.Invoke(this, EventArgs.Empty);
        AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
    }

    /// <summary>Set the value as if the user had moved the slider (used by − / + buttons).</summary>
    public void UserSetValue(int value) => SetValueCore(value, user: true);

    // ---------------------------------------------------------------- geometry

    private int ThumbSize => Dp(20);
    private bool Vertical => Orientation == Orientation.Vertical;

    /// <summary>Pixel range the thumb centre moves along.</summary>
    private (int Start, int End) Travel()
    {
        int half = ThumbSize / 2 + Dp(1);
        int length = Vertical ? Height : Width;
        return (half, Math.Max(half, length - half));
    }

    private int PositionOf(int value)
    {
        var (start, end) = Travel();
        if (_max == _min) return start;
        double t = (double)(value - _min) / (_max - _min);
        return Vertical ? (int)Math.Round(end - t * (end - start)) : (int)Math.Round(start + t * (end - start));
    }

    /// <summary>The value under a point on the rail (clamped to the selectable range).</summary>
    internal int ValueAt(Point p)
    {
        var (start, end) = Travel();
        double t = end == start ? 0 : ((Vertical ? end - p.Y : p.X - start) / (double)(end - start));
        return Clamp((int)Math.Round(_min + Math.Clamp(t, 0, 1) * (_max - _min)));
    }

    public override Size GetPreferredSize(Size proposedSize) =>
        Vertical ? new Size(Dp(32), Dp(PreferredLength)) : new Size(Dp(PreferredLength), Dp(32));

    // ---------------------------------------------------------------- input

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End
        || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && (e.Modifiers & (Keys.Control | Keys.Alt)) == 0 && HandleKey(e.KeyCode))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    /// <summary>Keyboard rules; returns whether the key belongs to the slider.</summary>
    internal bool HandleKey(Keys key)
    {
        int? target = key switch
        {
            Keys.Left or Keys.Down or Keys.OemMinus or Keys.Subtract => _value - SmallChange,
            Keys.Right or Keys.Up or Keys.Oemplus or Keys.Add => _value + SmallChange,
            Keys.PageDown => _value - LargeChange,
            Keys.PageUp => _value + LargeChange,
            Keys.Home => Floor,
            Keys.End => Ceiling,
            _ => null,
        };
        if (target is not { } v) return false;
        SetValueCore(v, user: true);
        return true;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !Enabled) return;
        _dragging = true;
        Capture = true;
        SetValueCore(ValueAt(e.Location), user: true);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) SetValueCore(ValueAt(e.Location), user: true);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging) return;
        _dragging = false;
        Capture = false;
        Released?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (_dragging && !Capture)
        {
            _dragging = false;
            Invalidate();
            Released?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---------------------------------------------------------------- painting

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool enabled = Enabled;
        int rail = Dp(4);
        var (start, end) = Travel();
        int cross = (Vertical ? Width : Height) / 2;
        int pos = PositionOf(_value);
        int origin = FillOrigin is { } o ? PositionOf(Math.Clamp(o, _min, _max)) : Vertical ? end : start;
        int bottom = Vertical ? end : start;

        Rectangle Seg(int a, int b) => Vertical
            ? new Rectangle(cross - rail / 2, Math.Min(a, b) - rail / 2, rail, Math.Abs(b - a) + rail)
            : new Rectangle(Math.Min(a, b) - rail / 2, cross - rail / 2, Math.Abs(b - a) + rail, rail);

        Shapes.FillRound(g, enabled ? P.Track : P.TextDisabled, Seg(start, end), rail / 2f);
        // Parts that can't be chosen look disabled.
        if (Floor > _min) Shapes.FillRound(g, P.MeterTrack, Seg(bottom, PositionOf(Floor)), rail / 2f);
        if (Ceiling < _max) Shapes.FillRound(g, P.MeterTrack, Seg(PositionOf(Ceiling), Vertical ? start : end), rail / 2f);
        Shapes.FillRound(g, enabled ? P.Accent : P.TextDisabled, Seg(origin, pos), rail / 2f);

        if (Mark is { } mark && mark > _min && mark < _max)
        {
            int m = PositionOf(mark);
            using var pen = new Pen(P.TextSecondary, Math.Max(1, Dp(1.5f)));
            if (Vertical) g.DrawLine(pen, cross - Dp(7), m, cross + Dp(7), m);
            else g.DrawLine(pen, m, cross - Dp(7), m, cross + Dp(7));
        }

        int thumb = ThumbSize;
        var center = Vertical ? new Point(cross, pos) : new Point(pos, cross);
        var outer = new Rectangle(center.X - thumb / 2, center.Y - thumb / 2, thumb, thumb);
        using (var b = new SolidBrush(P.Thumb)) g.FillEllipse(b, outer);
        using (var pen = new Pen(P.ThumbBorder, Hairline)) g.DrawEllipse(pen, RectangleF.Inflate(outer, -0.5f, -0.5f));
        float inner = !enabled ? Dp(10) : _dragging || IsPressed ? Dp(10) : IsHover ? Dp(14) : Dp(12);
        using (var b = new SolidBrush(enabled ? P.Accent : P.TextDisabled))
            g.FillEllipse(b, center.X - inner / 2f, center.Y - inner / 2f, inner, inner);

        if (FocusVisible) DrawFocusRing(g, new Rectangle(0, 0, Width, Height), Dp(4));
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new SliderAccessible(this);

    private sealed class SliderAccessible(FluentSlider owner) : ControlAccessibleObject(owner)
    {
        public override string? Value
        {
            get => owner.Value.ToString();
            set
            {
                if (int.TryParse(value, out var v)) owner.SetValueCore(v, user: true);
            }
        }
    }
}
