namespace HomePodCast.UI.Controls;

/// <summary>
/// Left navigation: one <see cref="NavItem"/> per page. Only the selected item is a tab stop; Up/Down
/// move between items and select them (like tabs), Enter/Space select the focused one.
/// </summary>
internal sealed class NavRail : LayoutPanel, ISurface
{
    private readonly List<NavItem> _items = [];
    private readonly List<NavItem> _footer = [];
    private readonly ToolTip _tips = new();
    private readonly Panel _viewport = new() { AutoScroll = true };
    private readonly StackPanel _main = new() { Gap = 4 };
    private readonly NavItem _themeButton = new(Glyph.Moon, "") { AccessibleRole = AccessibleRole.PushButton, TabStop = true };
    private int _selected = -1;

    public NavRail()
    {
        TabStop = false;
        AccessibleRole = AccessibleRole.PageTabList;
        _viewport.Controls.Add(_main);
        Controls.AddRange([_viewport, _themeButton]);
        _themeButton.Click += (_, _) => ThemeRequested?.Invoke();
        ShowTheme();
    }

    public Color SurfaceColor => Theme.P.IsDark ? Color.FromArgb(24, 24, 24) : Color.FromArgb(230, 230, 230);

    /// <summary>The user picked an item (mouse or keyboard); the argument is its index.</summary>
    public event Action<int>? Navigate;
    public event Action? ThemeRequested;

    private void ShowTheme()
    {
        string action = Theme.IsDark ? L.T("切换到浅色") : L.T("切换到深色");
        _themeButton.Text = Theme.IsDark ? L.T("浅色") : L.T("深色");
        _themeButton.Glyph = Theme.IsDark ? Glyph.Sun : Glyph.Moon;
        _themeButton.AccessibleName = action;
        _tips.SetToolTip(_themeButton, action);
    }
    protected override void OnThemeChanged()
    {
        ShowTheme();
        _viewport.BackColor = SurfaceColor;
        base.OnThemeChanged();
    }

    public IReadOnlyList<NavItem> Items => _items;

    public NavItem Add(string glyph, string text, bool bottom = false, string? label = null)
    {
        var item = new NavItem(glyph, label ?? text) { TabStop = false, AccessibleName = text };
        int index = _items.Count;
        item.Click += (_, _) => Select(index, user: true);
        item.KeyDown += (_, e) => OnItemKey(index, e);
        _items.Add(item);
        if (bottom) _footer.Add(item);
        _tips.SetToolTip(item, text); // compact navigation keeps the full page name on hover
        if (bottom) Controls.Add(item);
        else _main.Controls.Add(item);
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
        if (!_footer.Contains(_items[index])) EnsureVisible(_items[index]);
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
        int direction = e.KeyCode is Keys.Up or Keys.End ? -1 : 1;
        while (target >= 0 && target < _items.Count && _items[target].Collapsed) target += direction;
        if (target < 0 || target >= _items.Count || e.Modifiers != Keys.None) return;
        e.Handled = true;
        Select(target, user: true);
        _items[target].Focus();
    }

    /// <summary>Width that fits the longest label.</summary>
    public int PreferredWidth() => Math.Max(Dp(200), _items.Count == 0 ? 0 : _items.Max(i => i.GetPreferredSize(Size.Empty).Width) + Dp(16));

    /// <summary>Keep room for page controls at narrow widths; labels return as soon as both columns fit.</summary>
    public int WidthForWindow(int clientWidth)
    {
        int full = PreferredWidth();
        return full <= clientWidth / 3 && clientWidth - full >= Dp(560) ? full : Dp(64);
    }

    private void EnsureVisible(NavItem item)
    {
        int top = item.Top + _viewport.AutoScrollPosition.Y;
        int scroll = -_viewport.AutoScrollPosition.Y;
        if (top < 0) scroll += top;
        else if (top + item.Height > _viewport.ClientSize.Height) scroll += top + item.Height - _viewport.ClientSize.Height;
        if (scroll != -_viewport.AutoScrollPosition.Y) _viewport.AutoScrollPosition = new Point(0, Math.Max(0, scroll));
    }

    protected override int Arrange(int width, bool apply)
    {
        int step = Dp(42), inset = Dp(8);
        int footerHeight = step * (_footer.Count + 1);
        int available = Math.Max(0, Height - footerHeight - 2 * inset);
        int bodyHeight = _items.Except(_footer).Count(i => !i.Collapsed) * step - Dp(4);
        bool compact = width < PreferredWidth();
        if (apply)
        {
            foreach (var item in _items) item.Compact = compact;
            _themeButton.Compact = compact;
            _viewport.SetBounds(Dp(4), inset, Math.Max(0, width - Dp(8)), available);
            _viewport.BackColor = SurfaceColor;
            _viewport.AutoScrollMinSize = new Size(0, Math.Max(0, bodyHeight));
            int scrollWidth = bodyHeight > available ? SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi) : 0;
            Place(_main, 0, _viewport.AutoScrollPosition.Y, Math.Max(0, width - Dp(8) - scrollWidth), Math.Max(0, bodyHeight));
            int y = Math.Max(inset, Height - footerHeight - inset + Dp(4));
            Place(_themeButton, Dp(4), y, width - Dp(8), Dp(38));
            foreach (var item in _footer)
            {
                y += step;
                Place(item, Dp(4), y, width - Dp(8), Dp(38));
            }
        }
        return bodyHeight + footerHeight + 2 * inset;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(SurfaceColor);
        using var pen = new Pen(Theme.P.CardBorder, Math.Max(1, DeviceDpi / 96));
        e.Graphics.DrawLine(pen, Width - 1, 0, Width - 1, Height);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>A navigation entry: icon, text, hover/selected background and the accent pill when selected.</summary>
internal sealed class NavItem : FluentControl
{
    private bool _selected;
    private bool _compact;

    public NavItem(string glyph, string text)
    {
        SetStyle(ControlStyles.Selectable | ControlStyles.StandardClick, true);
        SetStyle(ControlStyles.StandardDoubleClick, false);
        AccessibleRole = AccessibleRole.PageTab;
        Glyph = glyph;
        Text = text;
    }

    public string Glyph { get; set; }

    public bool Compact
    {
        get => _compact;
        set { if (_compact != value) { _compact = value; Invalidate(); } }
    }

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
        int text = TextMeasure.Size(Text, Theme.FontFor(TextStyle.Body, DeviceDpi, Text), Size.Empty, TextFlags.Measure).Width;
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
        int x = _compact ? (Width - Dp(16)) / 2 : Dp(16);
        Shapes.Glyph(g, Glyph, Theme.IconFont(16, DeviceDpi), new Rectangle(x, 0, Dp(16), Height), P.Text);
        if (!_compact)
        {
            x += Dp(16 + 14);
            TextRenderer.DrawText(g, Text, Theme.FontFor(TextStyle.Body, DeviceDpi, Text), new Rectangle(x, 0, Width - x - Dp(8), Height), P.Text, TextFlags.Line);
        }
        if (FocusVisible) DrawFocusRing(g, r, Dp(5));
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new NavAccessible(this);

    private sealed class NavAccessible(NavItem owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleStates State =>
            base.State | (owner.AccessibleRole is AccessibleRole.PageTab or AccessibleRole.ListItem ? AccessibleStates.Selectable : AccessibleStates.None) | (owner.Selected ? AccessibleStates.Selected : AccessibleStates.None);

        public override string DefaultAction => owner.AccessibleRole is AccessibleRole.PageTab or AccessibleRole.ListItem ? L.T("选择") : owner.AccessibleName ?? owner.Text;

        public override void DoDefaultAction() => owner.OnClick(EventArgs.Empty);
    }
}
