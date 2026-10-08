using System.Collections.ObjectModel;

namespace HomePodCast.UI.Controls;

/// <summary>
/// Fluent drop-down list (no typing): shows the selected item and a chevron; opens a themed menu.
/// Up/Down/Home/End change the selection while closed; Space, Enter, F4 or Alt+Down open the list.
/// <see cref="SelectedIndexChanged"/> fires for every change, <see cref="SelectionChangeCommitted"/> only for the user's.
/// </summary>
internal sealed class FluentComboBox : FluentControl
{
    private int _selected = -1;
    private ContextMenuStrip? _menu;
    private long _closedAt;

    public FluentComboBox()
    {
        SetStyle(ControlStyles.Selectable | ControlStyles.StandardClick, true);
        TabStop = true;
        AccessibleRole = AccessibleRole.ComboBox;
        Items = new ItemList(this);
    }

    public ItemList Items { get; }

    public event EventHandler? SelectedIndexChanged;
    public event EventHandler? SelectionChangeCommitted;

    /// <summary>Width in 96-DPI units for <see cref="GetPreferredSize"/>; 0 = as wide as the longest item.</summary>
    public int PreferredWidth { get; set; }

    /// <summary>Least width in 96-DPI units when sized to the items (<see cref="PreferredWidth"/> = 0).</summary>
    public int MinWidth { get; set; }

    public int SelectedIndex
    {
        get => _selected;
        set => Select(value, user: false);
    }

    public string? SelectedText => _selected >= 0 && _selected < Items.Count ? Items[_selected] : null;

    public bool DroppedDown => _menu is { Visible: true };

    private void Select(int index, bool user)
    {
        if (index < -1 || index >= Items.Count) index = -1;
        if (index == _selected) return;
        _selected = index;
        Invalidate();
        SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        if (user) SelectionChangeCommitted?.Invoke(this, EventArgs.Empty);
        AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
    }

    private void ItemsChanged()
    {
        if (_selected >= Items.Count) _selected = -1;
        Invalidate();
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        int h = Dp(32);
        if (PreferredWidth > 0) return new Size(Dp(PreferredWidth), h);
        int text = Items.Count == 0 ? Dp(80)
            : Items.Max(t => TextRenderer.MeasureText(t, Theme.FontFor(TextStyle.Body, DeviceDpi, t), Size.Empty, TextFlags.Measure).Width);
        return new Size(Math.Max(text + Dp(12 + 38), Dp(MinWidth)), h);
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        // A click on the box while the list is open closes the list first; don't reopen it at once.
        if (DroppedDown || Environment.TickCount64 - _closedAt < 300) return;
        Open();
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.Enter or Keys.Space or Keys.F4 || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (HandleKey(e.KeyData))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    internal bool HandleKey(Keys keyData)
    {
        if (Items.Count == 0) return false;
        switch (keyData)
        {
            case Keys.Up: Select(Math.Max(0, _selected - 1), user: true); return true;
            case Keys.Down: Select(Math.Min(Items.Count - 1, _selected + 1), user: true); return true;
            case Keys.Home: Select(0, user: true); return true;
            case Keys.End: Select(Items.Count - 1, user: true); return true;
            case Keys.Space or Keys.Enter or Keys.F4 or (Keys.Alt | Keys.Down):
                Open();
                return true;
            default: return false;
        }
    }

    private void Open()
    {
        if (!Enabled || Items.Count == 0) return;
        _menu?.Dispose();
        var menu = new ContextMenuStrip
        {
            Renderer = new FluentMenuRenderer(),
            ShowImageMargin = false,
            ShowCheckMargin = false,
            Font = StyleFont(TextStyle.Body),
            Padding = new Padding(0, Dp(4), 0, Dp(4)),
            MinimumSize = new Size(Width, 0),
        };
        for (int i = 0; i < Items.Count; i++)
        {
            int index = i;
            var item = new ToolStripMenuItem(Items[i])
            {
                Checked = i == _selected,
                Font = Theme.FontFor(TextStyle.Body, DeviceDpi, Items[i]),
                Padding = new Padding(Dp(8), Dp(6), Dp(8), Dp(6)),
                AutoSize = true,
            };
            item.Click += (_, _) => Select(index, user: true);
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) =>
        {
            _closedAt = Environment.TickCount64;
            Invalidate();
        };
        menu.HandleCreated += (_, _) => Theme.RoundPopup(menu.Handle);
        _menu = menu;
        menu.Show(this, new Point(0, Height + Dp(2)));
        if (_selected >= 0 && _selected < menu.Items.Count) menu.Items[_selected].Select();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = new Rectangle(0, 0, Width, Height);
        float radius = Dp(4);
        bool enabled = Enabled;
        var fill = !enabled ? P.Control : IsPressed || DroppedDown ? P.ControlPressed : IsHover ? P.ControlHover : P.Control;
        Shapes.FillRound(g, fill, r, radius);
        Shapes.BorderRound(g, P.ControlBorder, r, radius, Hairline);
        var textRect = new Rectangle(Dp(11), 0, Width - Dp(11 + 36), Height);
        TextRenderer.DrawText(g, SelectedText ?? "", Theme.FontFor(TextStyle.Body, DeviceDpi, SelectedText), textRect, enabled ? P.Text : P.TextDisabled, TextFlags.Line);
        Shapes.Glyph(g, Glyph.ChevronDown, Theme.IconFont(12, DeviceDpi), new Rectangle(Width - Dp(32), 0, Dp(20), Height),
            enabled ? P.TextSecondary : P.TextDisabled);
        if (FocusVisible) DrawFocusRing(g, r, radius + Dp(1));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _menu?.Dispose();
        base.Dispose(disposing);
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new ComboAccessible(this);

    private sealed class ComboAccessible(FluentComboBox owner) : ControlAccessibleObject(owner)
    {
        /// <summary>Setting the value picks the item with that text (as the user would).</summary>
        public override string? Value
        {
            get => owner.SelectedText;
            set
            {
                int index = value == null ? -1 : owner.Items.IndexOf(value);
                if (index >= 0 && owner.Enabled) owner.Select(index, user: true);
            }
        }

        public override string DefaultAction => L.T("打开");
        public override void DoDefaultAction() => owner.Open();
    }

    /// <summary>The items; changing them repaints the box (the selection is kept when still in range).</summary>
    public sealed class ItemList(FluentComboBox owner) : Collection<string>
    {
        protected override void InsertItem(int index, string item)
        {
            base.InsertItem(index, item);
            owner.ItemsChanged();
        }

        protected override void SetItem(int index, string item)
        {
            base.SetItem(index, item);
            owner.ItemsChanged();
        }

        protected override void RemoveItem(int index)
        {
            base.RemoveItem(index);
            owner.ItemsChanged();
        }

        protected override void ClearItems()
        {
            base.ClearItems();
            owner.ItemsChanged();
        }

        public void AddRange(IEnumerable<string> items)
        {
            foreach (var item in items) Add(item);
        }
    }
}

/// <summary>Menus (combo box lists) in the theme's colours with rounded highlights.</summary>
internal sealed class FluentMenuRenderer : ToolStripRenderer
{
    private static Theme.Palette P => Theme.P;
    private static int Dp(ToolStrip? s, float v) => Theme.Dp(v, s?.DeviceDpi ?? 96);

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(P.Popup);

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (Environment.OSVersion.Version.Build >= 22000) return; // Windows 11 draws the rounded border
        using var pen = new Pen(P.PopupBorder);
        e.Graphics.DrawRectangle(pen, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var item = e.Item;
        var r = new Rectangle(Dp(e.ToolStrip, 4), 1, item.Width - Dp(e.ToolStrip, 8), item.Height - 2);
        if (item.Selected && item.Enabled) Shapes.FillRound(e.Graphics, P.Subtle, r, Dp(e.ToolStrip, 4));
        if (item is ToolStripMenuItem { Checked: true } && e.ToolStrip is ContextMenuStrip { ShowCheckMargin: false, ShowImageMargin: false })
        {
            // Selected entry of a drop-down list: accent pill on the left, like Windows 11.
            int h = Dp(e.ToolStrip, 16), w = Dp(e.ToolStrip, 3);
            Shapes.FillRound(e.Graphics, P.Accent, new Rectangle(r.X, (item.Height - h) / 2, w, h), w / 2f);
        }
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? P.Text : P.TextDisabled;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var font = Theme.IconFont(12, e.ToolStrip?.DeviceDpi ?? 96);
        Shapes.Glyph(e.Graphics, Glyph.CheckMark, font, e.ImageRectangle, e.Item.Enabled ? P.Text : P.TextDisabled);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item?.Enabled == false ? P.TextDisabled : P.TextSecondary;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using var pen = new Pen(P.Divider);
        e.Graphics.DrawLine(pen, Dp(e.ToolStrip, 4), y, e.Item.Width - Dp(e.ToolStrip, 4), y);
    }
}
