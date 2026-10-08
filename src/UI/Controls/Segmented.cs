namespace HomePodCast.UI.Controls;

/// <summary>
/// Segmented control (one choice out of a few): the selected segment is filled with the accent.
/// Left/Right/Home/End move the selection. <see cref="SelectedIndexChanged"/> fires for every change,
/// <see cref="SelectionChangeCommitted"/> only for the user's.
/// </summary>
internal sealed class Segmented : FluentControl
{
    private string[] _items = [];
    private int _selected = -1, _hot = -1;

    public Segmented(params string[] items)
    {
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        AccessibleRole = AccessibleRole.List;
        _items = items;
        if (items.Length > 0) _selected = 0;
    }

    public event EventHandler? SelectedIndexChanged;
    public event EventHandler? SelectionChangeCommitted;

    /// <summary>Segments as wide as the widest (true), or each as wide as its text.</summary>
    public bool EqualWidths { get; set; } = true;

    /// <summary>Smaller text and height (rows of the mixer).</summary>
    public bool Compact { get; set; }

    public IReadOnlyList<string> Items
    {
        get => _items;
        set
        {
            _items = value.ToArray();
            if (_selected >= _items.Length) _selected = _items.Length - 1;
            Invalidate();
            RequestLayout();
        }
    }

    public int SelectedIndex
    {
        get => _selected;
        set => Select(value, user: false);
    }

    public string? SelectedText => _selected >= 0 && _selected < _items.Length ? _items[_selected] : null;

    private void Select(int index, bool user)
    {
        if (index < -1 || index >= _items.Length || index == _selected) return;
        _selected = index;
        Invalidate();
        SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        if (user) SelectionChangeCommitted?.Invoke(this, EventArgs.Empty);
        AccessibilityNotifyClients(AccessibleEvents.Selection, index);
    }

    private TextStyle Style => Compact ? TextStyle.Caption : TextStyle.Body;

    private int[] Widths() => Widths(_items, Compact, EqualWidths, DeviceDpi);

    private static int[] Widths(IReadOnlyList<string> items, bool compact, bool equal, int dpi)
    {
        var font = Theme.Font(compact ? TextStyle.Caption : TextStyle.Body, dpi);
        int pad = Theme.Dp(compact ? 10 : 14, dpi);
        var w = items.Select(t => TextRenderer.MeasureText(t, font, Size.Empty, TextFlags.Measure).Width + pad * 2).ToArray();
        if (equal && w.Length > 0)
        {
            int max = w.Max();
            Array.Fill(w, max);
        }
        return w;
    }

    /// <summary>Preferred width of a segmented control with these items (to line columns up).</summary>
    public static int PreferredWidthFor(IReadOnlyList<string> items, bool compact, bool equal, int dpi) =>
        Widths(items, compact, equal, dpi).Sum() + Theme.Dp(6, dpi);

    /// <summary>Segment rectangles, stretched to fill the control's width.</summary>
    private Rectangle[] Segments()
    {
        var w = Widths();
        int pad = Dp(3);
        int total = w.Sum();
        int avail = Width - pad * 2;
        var result = new Rectangle[w.Length];
        double x = pad;
        for (int i = 0; i < w.Length; i++)
        {
            double share = total > 0 ? (double)w[i] / total * avail : 0;
            result[i] = new Rectangle((int)Math.Round(x), pad, (int)Math.Round(x + share) - (int)Math.Round(x), Height - pad * 2);
            x += share;
        }
        return result;
    }

    public override Size GetPreferredSize(Size proposedSize) =>
        new(Widths().Sum() + Dp(6), Dp(Compact ? 30 : 34));

    internal int IndexAt(Point p) => Array.FindIndex(Segments(), r => r.Contains(p));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hot = IndexAt(e.Location);
        if (hot != _hot)
        {
            _hot = hot;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hot = -1;
        base.OnMouseLeave(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && Enabled && IndexAt(e.Location) is var i and >= 0) Select(i, user: true);
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Modifiers != Keys.None || _items.Length == 0) return;
        if (HandleKey(e.KeyCode))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    internal bool HandleKey(Keys key)
    {
        int target = key switch
        {
            Keys.Left => Math.Max(0, _selected - 1),
            Keys.Right => Math.Min(_items.Length - 1, _selected + 1),
            Keys.Home => 0,
            Keys.End => _items.Length - 1,
            _ => -2,
        };
        if (target == -2) return false;
        Select(target, user: true);
        return true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        bool enabled = Enabled;
        var outer = new Rectangle(0, 0, Width, Height);
        float radius = Dp(6);
        Shapes.FillRound(g, P.Control, outer, radius);
        Shapes.BorderRound(g, P.ControlBorder, outer, radius, Hairline);
        var font = StyleFont(Style);
        var segs = Segments();
        for (int i = 0; i < segs.Length; i++)
        {
            var r = segs[i];
            bool selected = i == _selected;
            Color fore;
            if (selected)
            {
                Shapes.FillRound(g, enabled ? P.Accent : P.TextDisabled, r, Dp(4));
                fore = enabled ? P.TextOnAccent : P.Card;
            }
            else
            {
                if (enabled && i == _hot) Shapes.FillRound(g, IsPressed ? P.SubtlePressed : P.Subtle, r, Dp(4));
                fore = enabled ? P.Text : P.TextDisabled;
            }
            TextRenderer.DrawText(g, _items[i], font, r, fore, TextFlags.Line | TextFormatFlags.HorizontalCenter);
        }
        if (FocusVisible) DrawFocusRing(g, outer, radius);
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new SegmentedAccessible(this);

    private sealed class SegmentedAccessible(Segmented owner) : ControlAccessibleObject(owner)
    {
        public override string? Value => owner.SelectedText;
        public override int GetChildCount() => owner._items.Length;
        public override AccessibleObject? GetChild(int index) =>
            index >= 0 && index < owner._items.Length ? new SegmentAccessible(owner, this, index) : null;
        public override AccessibleObject? GetSelected() => owner._selected >= 0 ? GetChild(owner._selected) : null;
    }

    private sealed class SegmentAccessible(Segmented owner, AccessibleObject parent, int index) : AccessibleObject
    {
        public override string? Name => owner._items[index];
        public override AccessibleRole Role => AccessibleRole.RadioButton;
        public override AccessibleObject Parent => parent;
        public override AccessibleStates State =>
            AccessibleStates.Selectable | (index == owner._selected ? AccessibleStates.Checked | AccessibleStates.Selected : AccessibleStates.None);
        public override Rectangle Bounds => owner.RectangleToScreen(owner.Segments()[index]);
        public override string DefaultAction => L.T("选择");
        public override void DoDefaultAction() => owner.Select(index, user: true);
    }
}
