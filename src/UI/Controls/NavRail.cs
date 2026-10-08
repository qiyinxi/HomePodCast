namespace HomePodCast.UI.Controls;

/// <summary>
/// Left navigation: one <see cref="NavItem"/> per page. Only the selected item is a tab stop; Up/Down
/// move between items and select them (like tabs), Enter/Space select the focused one.
/// </summary>
internal sealed class NavRail : LayoutPanel, ISurface
{
    private readonly List<NavItem> _items = [];
    private int _selected = -1;

    public NavRail()
    {
        TabStop = false;
        AccessibleRole = AccessibleRole.PageTabList;
    }

    public Color SurfaceColor => Theme.P.Window;

    /// <summary>The user picked an item (mouse or keyboard); the argument is its index.</summary>
    public event Action<int>? Navigate;

    public IReadOnlyList<NavItem> Items => _items;

    public NavItem Add(string glyph, string text)
    {
        var item = new NavItem(glyph, text) { TabStop = false };
        int index = _items.Count;
        item.Click += (_, _) => Select(index, user: true);
        item.KeyDown += (_, e) => OnItemKey(index, e);
        _items.Add(item);
        Controls.Add(item);
        return item;
    }

    public int SelectedIndex
    {
        get => _selected;
        set => Select(value, user: false);
    }

    private void Select(int index, bool user)
    {
        if (index < 0 || index >= _items.Count) return;
        bool changed = index != _selected;
        _selected = index;
        for (int i = 0; i < _items.Count; i++)
        {
            _items[i].Selected = i == index;
            _items[i].TabStop = i == index;
        }
        if (user && changed) Navigate?.Invoke(index);
    }

    private void OnItemKey(int index, KeyEventArgs e)
    {
        int target = e.KeyCode switch
        {
            Keys.Up => index - 1,
            Keys.Down => index + 1,
            Keys.Home => 0,
            Keys.End => _items.Count - 1,
            _ => -1,
        };
        if (target < 0 || target >= _items.Count || e.Modifiers != Keys.None) return;
        e.Handled = true;
        Select(target, user: true);
        _items[target].Focus();
    }

    /// <summary>Width that fits the longest label.</summary>
    public int PreferredWidth() => Math.Max(Dp(200), _items.Count == 0 ? 0 : _items.Max(i => i.GetPreferredSize(Size.Empty).Width) + Dp(16));

    protected override int Arrange(int width, bool apply)
    {
        int y = Dp(8), x = Dp(4), w = width - Dp(8);
        foreach (var item in _items)
        {
            int h = item.GetPreferredSize(Size.Empty).Height;
            if (apply) Place(item, x, y, w, h);
            y += h + Dp(4);
        }
        return y;
    }

    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(SurfaceColor);
}

/// <summary>A navigation entry: icon, text, hover/selected background and the accent pill when selected.</summary>
internal sealed class NavItem : FluentControl
{
    private bool _selected;

    public NavItem(string glyph, string text)
    {
        SetStyle(ControlStyles.Selectable | ControlStyles.StandardClick, true);
        SetStyle(ControlStyles.StandardDoubleClick, false);
        AccessibleRole = AccessibleRole.PageTab;
        Glyph = glyph;
        Text = text;
    }

    public string Glyph { get; }

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
        }
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.KeyCode is Keys.Enter or Keys.Space && e.Modifiers == Keys.None)
        {
            e.Handled = true;
            OnClick(EventArgs.Empty);
        }
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        int text = TextRenderer.MeasureText(Text, StyleFont(TextStyle.Body), Size.Empty, TextFlags.Measure).Width;
        return new Size(Dp(16 + 16 + 14) + text + Dp(16), Dp(38));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = new Rectangle(0, 0, Width, Height);
        var fill = _selected ? (IsHover ? P.NavHover : P.NavSelected) : IsPressed ? P.NavSelected : IsHover ? P.NavHover : Color.Empty;
        if (!fill.IsEmpty) Shapes.FillRound(g, fill, r, Dp(4));
        if (_selected)
        {
            int h = Dp(16), w = Dp(3);
            Shapes.FillRound(g, P.Accent, new Rectangle(Dp(3), (Height - h) / 2, w, h), w / 2f); // clear of the focus ring
        }
        int x = Dp(16);
        Shapes.Glyph(g, Glyph, Theme.IconFont(16, DeviceDpi), new Rectangle(x, 0, Dp(16), Height), P.Text);
        x += Dp(16 + 14);
        TextRenderer.DrawText(g, Text, StyleFont(TextStyle.Body), new Rectangle(x, 0, Width - x - Dp(8), Height), P.Text, TextFlags.Line);
        if (FocusVisible) DrawFocusRing(g, r, Dp(5));
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new NavAccessible(this);

    private sealed class NavAccessible(NavItem owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleStates State =>
            base.State | AccessibleStates.Selectable | (owner.Selected ? AccessibleStates.Selected : AccessibleStates.None);

        public override string DefaultAction => L.T("选择");

        public override void DoDefaultAction() => owner.OnClick(EventArgs.Empty);
    }
}
