using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace HomePodCast.UI.Controls;

/// <summary>A container whose background the controls on it paint behind themselves.</summary>
internal interface ISurface
{
    Color SurfaceColor { get; }
}

/// <summary>
/// Base of the custom-drawn controls: double-buffered, repaints on theme changes, tracks hover/pressed,
/// paints the surface it sits on, and sizes everything from <see cref="Control.DeviceDpi"/> (96-DPI units
/// through <see cref="Dp"/>; never raw pixels). Texts use <see cref="Theme.Font"/> for the control's DPI.
/// </summary>
internal abstract class FluentControl : Control
{
    private bool _hover, _pressed, _collapsed;

    protected FluentControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, false); // interactive controls opt in
        TabStop = false;
        Theme.Changed += ThemeChanged;
    }

    protected bool IsHover => _hover;
    protected bool IsPressed => _pressed;
    protected static Theme.Palette P => Theme.P;
    protected int Dp(float value) => Theme.Dp(value, DeviceDpi);

    /// <summary>One device pixel per 96 DPI step: hairline borders stay crisp at 125 % and 150 %.</summary>
    protected int Hairline => Math.Max(1, DeviceDpi / 96);

    protected Font StyleFont(TextStyle style) => Theme.Font(style, DeviceDpi);

    /// <summary>Hidden and left out of the layout (unlike Visible, which also turns false while the page is hidden).</summary>
    public bool Collapsed
    {
        get => _collapsed;
        set
        {
            if (_collapsed == value) return;
            _collapsed = value;
            Visible = !value;
            LayoutPanel.Relayout(Parent);
        }
    }

    /// <summary>The colour behind this control (the nearest card or page).</summary>
    protected Color Surface => SurfaceOf(Parent);

    public static Color SurfaceOf(Control? c)
    {
        for (; c != null; c = c.Parent)
            if (c is ISurface s) return s.SurfaceColor;
        return Theme.P.Window;
    }

    /// <summary>Keyboard focus is shown only after keyboard use (Windows' focus-cue rule).</summary>
    protected bool FocusVisible => Focused && ShowFocusCues;

    private void ThemeChanged()
    {
        if (!IsDisposed) OnThemeChanged();
    }

    protected virtual void OnThemeChanged() => Invalidate();

    /// <summary>Ask the page to measure again (text or size of this control changed).</summary>
    protected void RequestLayout() => LayoutPanel.Relayout(Parent);

    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(Surface);

    protected override void WndProc(ref Message m)
    {
        LayoutPanel.KeepChildOrder(ref m);
        base.WndProc(ref m);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            if (TabStop && CanFocus) Focus();
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        Invalidate();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnLostFocus(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    /// <summary>
    /// Only a repaint: the window lays itself out once at the new DPI (MainWindow.OnDpiChanged and the dialogs; WinForms'
    /// font scaling for the others). Windows sends this to every child window, top-down, after the window's WM_DPICHANGED;
    /// asking for a layout here laid the whole page out once per control. 2026-10-15, the main window moved between the
    /// maintainer's 150 % and 100 % monitors: about 8 s 「未响应」 per crossing (282 page layouts, 227 000 text measurements).
    /// </summary>
    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= ThemeChanged;
        base.Dispose(disposing);
    }

    /// <summary>Fluent focus visual: a 2 px ring (black/white) with a 1 px inner contrast line.</summary>
    protected void DrawFocusRing(Graphics g, Rectangle bounds, float radius)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float w = Dp(2);
        var outer = RectangleF.Inflate(bounds, -w / 2f, -w / 2f);
        using (var pen = new Pen(P.FocusOuter, w)) Shapes.DrawRound(g, pen, outer, radius);
        var inner = RectangleF.Inflate(outer, -w / 2f - 0.5f, -w / 2f - 0.5f);
        using (var pen = new Pen(P.FocusInner, 1f)) Shapes.DrawRound(g, pen, inner, Math.Max(0, radius - w));
    }

    /// <summary>Single-line text height for a style.</summary>
    protected int LineHeight(TextStyle style) => TextMeasure.Size("Ag国", StyleFont(style), Size.Empty, TextFlags.Measure).Height;
}

internal static class TextFlags
{
    public const TextFormatFlags Measure = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
    public const TextFormatFlags Line = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine |
                                        TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
    public const TextFormatFlags Wrap = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak |
                                        TextFormatFlags.TextBoxControl;
}

/// <summary>Rounded rectangles that stay crisp (AA, half-pixel aligned).</summary>
internal static class Shapes
{
    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRound(Graphics g, Color color, RectangleF r, float radius)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        using var path = Round(r, radius);
        g.FillPath(brush, path);
    }

    /// <summary>A 1-device-pixel border inside <paramref name="r"/>.</summary>
    public static void BorderRound(Graphics g, Color color, RectangleF r, float radius, float width = 1f)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, width);
        DrawRound(g, pen, RectangleF.Inflate(r, -width / 2f, -width / 2f), radius);
    }

    public static void DrawRound(Graphics g, Pen pen, RectangleF r, float radius)
    {
        using var path = Round(r, radius);
        g.DrawPath(pen, path);
    }

    /// <summary>A card: fill and hairline border.</summary>
    public static void Card(Graphics g, Rectangle r, float radius, Color fill, Color border, float hairline)
    {
        FillRound(g, fill, r, radius);
        BorderRound(g, border, r, radius, hairline);
    }

    public static void Glyph(Graphics g, string glyph, Font font, Rectangle r, Color color) =>
        TextRenderer.DrawText(g, glyph, font, r, color,
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine);
}

/// <summary>
/// Container that lays its children out itself, in device pixels for its DPI: <see cref="Arrange"/>
/// measures (apply: false) or positions (apply: true) for a width and returns the height it needs.
/// Pages, cards and rows are built from these; nothing relies on WinForms' AutoScale.
/// </summary>
internal abstract class LayoutPanel : Panel
{
    private bool _collapsed;
    private TextAutomation? _textAutomation;
    private bool _releasingTextAutomation;

    protected LayoutPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        Theme.Changed += ThemeChanged;
    }

    protected static Theme.Palette P => Theme.P;
    protected int Dp(float value) => Theme.Dp(value, DeviceDpi);

    public bool Collapsed
    {
        get => _collapsed;
        set
        {
            if (_collapsed == value) return;
            _collapsed = value;
            Visible = !value;
            Relayout(Parent);
        }
    }

    /// <summary>Position the children for <paramref name="width"/> when <paramref name="apply"/>; return the height needed.</summary>
    protected abstract int Arrange(int width, bool apply);

    public override Size GetPreferredSize(Size proposedSize)
    {
        int width = proposedSize.Width > 0 ? proposedSize.Width : Width;
        return new Size(width, Arrange(width, apply: false));
    }

    /// <summary>Tests: called on this thread for every container (page, card, row) that places its children.</summary>
    [ThreadStatic] internal static Action<Control>? Arranged;

    protected override void OnLayout(LayoutEventArgs levent)
    {
        // Not while a subclass is still being built (Controls.Add lays out): only once it is on a parent.
        if (Width <= 0 || Parent == null) return;
        Arranged?.Invoke(this);
        Arrange(Width, apply: true);
    }

    protected virtual void OnThemeChanged() => Invalidate();

    private void ThemeChanged()
    {
        if (!IsDisposed) OnThemeChanged();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= ThemeChanged;
        base.Dispose(disposing);
    }

    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(FluentControl.SurfaceOf(this));

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        foreach (var text in Controls.OfType<TextBlock>())
            if (text.ParentPainted) text.DrawInParent(e.Graphics);
    }

    protected override void OnControlRemoved(ControlEventArgs e)
    {
        base.OnControlRemoved(e);
        if (e.Control is TextBlock { ParentPainted: true })
        {
            Invalidate(e.Control.Bounds);
            if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.Reorder, -1);
            _textAutomation?.Notify((TextBlock)e.Control, AccessibleEvents.Reorder);
        }
    }

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        if (e.Control is TextBlock { ParentPainted: true } text && IsHandleCreated)
        {
            Invalidate(text.Bounds);
            PaintedTextChanged(text, AccessibleEvents.Reorder);
        }
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new LayoutAccessible(this);

    internal void PaintedTextChanged(TextBlock text, AccessibleEvents change)
    {
        if (!IsHandleCreated) return;
        _textAutomation?.Notify(text, change);
        if (change == AccessibleEvents.Reorder) AccessibilityNotifyClients(change, -1);
        else if (AccessibilityObject is LayoutAccessible accessible)
        {
            int index = Array.IndexOf(accessible.Children, text);
            if (index >= 0) AccessibilityNotifyClients(change, index);
        }
    }

    private sealed class LayoutAccessible(LayoutPanel owner) : ControlAccessibleObject(owner)
    {
        private bool HasPaintedText => owner.Controls.OfType<TextBlock>().Any(t => t.ParentPainted);
        // Native children keep their providers and keyboard behavior. Painted text occupies its original layout position.
        internal Control[] Children => owner.Controls.Cast<Control>()
            .Where(c => c is TextBlock { ParentPainted: true } t ? t.Visible && !t.Collapsed : c.IsHandleCreated).ToArray();
        public override int GetChildCount() => HasPaintedText ? Children.Length : base.GetChildCount();
        public override AccessibleObject? GetChild(int index)
        {
            if (!HasPaintedText) return base.GetChild(index);
            var children = Children;
            return index >= 0 && index < children.Length ? children[index].AccessibilityObject : null;
        }
        public override AccessibleObject? HitTest(int x, int y)
        {
            foreach (var text in owner.Controls.OfType<TextBlock>())
                if (text.ParentPainted && text.Visible && text.AccessibilityObject.HitTest(x, y) is { } hit) return hit;
            return base.HitTest(x, y);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x003D && unchecked((int)m.LParam.ToInt64()) == -25 && _releasingTextAutomation)
        {
            m.Result = IntPtr.Zero;
            return;
        }
        if (m.Msg == 0x003D && unchecked((int)m.LParam.ToInt64()) == -25 && (AccessibleRole == AccessibleRole.Default
                || _textAutomation != null || Controls.OfType<TextBlock>().Any(t => t.ParentPainted)))
        {
            _textAutomation ??= new TextAutomation(this);
            m.Result = System.Windows.Automation.Provider.AutomationInteropProvider.ReturnRawElementProvider(
                Handle, m.WParam, m.LParam, _textAutomation);
            return;
        }
        KeepChildOrder(ref m);
        base.WndProc(ref m);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        var provider = _textAutomation;
        _textAutomation = null;
        _releasingTextAutomation = true;
        try
        {
            provider?.Disconnect();
            base.OnHandleDestroyed(e);
        }
        finally { _releasingTextAutomation = false; }
    }

    /// <summary>
    /// A container's order of children is its layout order, and stays as the code set it. When a child window's z-order
    /// changes (WM_WINDOWPOSCHANGED), WinForms re-sorts the parent's children to match it, and the window of a row collapsed
    /// while the page was built is created when the row is first shown, after the last earlier sibling that already has one.
    /// 2026-10-15: turning 设置 →「夜间自动断开」 on put 「闲置多久后断开」 above 「时段」 (the second row was shown first).
    /// Marked SWP_NOZORDER, the message leaves the order alone (our children never overlap).
    /// </summary>
    internal static void KeepChildOrder(ref Message m)
    {
        const int WM_WINDOWPOSCHANGED = 0x0047, SWP_NOZORDER = 0x0004;
        if (m.Msg != WM_WINDOWPOSCHANGED || m.LParam == IntPtr.Zero) return;
        int offset = 2 * IntPtr.Size + 16; // WINDOWPOS: hwnd, hwndInsertAfter, x, y, cx, cy, flags
        int flags = Marshal.ReadInt32(m.LParam, offset);
        if ((flags & SWP_NOZORDER) == 0) Marshal.WriteInt32(m.LParam, offset, flags | SWP_NOZORDER);
    }

    public static bool IsCollapsed(Control c) => c is FluentControl { Collapsed: true } or LayoutPanel { Collapsed: true };

    /// <summary>Height a child wants at <paramref name="width"/> (0 when collapsed).</summary>
    public static int HeightFor(Control c, int width) => IsCollapsed(c) ? 0 : c.GetPreferredSize(new Size(width, 0)).Height;

    public static int WidthFor(Control c) => IsCollapsed(c) ? 0 : c.GetPreferredSize(Size.Empty).Width;

    /// <summary>
    /// The windows that showing the hidden <paramref name="root"/> would create (it and every control in it that is not
    /// collapsed), created now, parents first, while it stays hidden. <paramref name="yield"/> is asked after each one: true
    /// stops there, and the next call goes on from there. True once they all exist.
    /// </summary>
    internal static bool CreateWindows(Control root, Func<bool> yield)
    {
        if (root is TextBlock { ParentPainted: true }) return true;
        if (!root.IsHandleCreated)
        {
            _ = root.Handle;
            if (yield()) return false;
        }
        foreach (var child in root.Controls.Cast<Control>().ToArray())
            if (!IsCollapsed(child) && !CreateWindows(child, yield)) return false;
        return true;
    }

    /// <summary>Set bounds and arrange a container once, including when only its children's sizes changed.</summary>
    public static void Place(Control c, int x, int y, int w, int h)
    {
        bool container = c is LayoutPanel or ScrollPage;
        if (container) c.SuspendLayout();
        try
        {
            if (c.Left != x || c.Top != y || c.Width != w || c.Height != h) c.SetBounds(x, y, Math.Max(0, w), Math.Max(0, h));
        }
        finally
        {
            // SetBounds already requests layout on a resized control. Let the explicit pass below handle it too:
            // doing both multiplies the work at each level of the page's nested cards and rows.
            if (container) c.ResumeLayout(false);
        }
        if (container) c.PerformLayout();
    }

    /// <summary>Apply related text/visibility changes together, then measure and position the subtree once.</summary>
    public static IDisposable BatchLayout(Control root) => new LayoutBatch(root);

    /// <summary>
    /// A window's WM_DPICHANGED (<paramref name="scale"/>: base.OnDpiChanged and the window's own font and sizes for the new
    /// DPI), with one layout at the end. Otherwise the font that WinForms and the window set reaches every control, and each
    /// control asks its container for a layout: every card and row laid out again, once per control below it.
    /// </summary>
    public static void ScaleForDpi(Form window, Action scale)
    {
        using (BatchLayout(window)) scale();
        window.Invalidate(true);
    }

    /// <summary>
    /// Like <see cref="BatchLayout"/>, but without the layout at the end: what changes meanwhile is laid out by whoever
    /// lays <paramref name="root"/> out next (MainWindow after a DPI change, a hidden page when it is prepared or shown).
    /// </summary>
    public static IDisposable SuspendLayouts(Control root) => new LayoutBatch(root, layout: false);

    private sealed class LayoutBatch : IDisposable
    {
        private readonly Control _root;
        private readonly bool _layout;
        private readonly List<Control> _containers = [];
        private bool _disposed;

        public LayoutBatch(Control root, bool layout = true)
        {
            _root = root;
            _layout = layout;
            Suspend(root);
        }

        private void Suspend(Control c)
        {
            if (c == _root || c is LayoutPanel or ScrollPage)
            {
                c.SuspendLayout();
                _containers.Add(c);
            }
            foreach (Control child in c.Controls) Suspend(child);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var c in _containers) c.ResumeLayout(false);
            if (_layout && !_root.IsDisposed) _root.PerformLayout();
        }
    }

    /// <summary>Measure and position everything again, from the outermost layout container up from <paramref name="from"/>.</summary>
    public static void Relayout(Control? from) => RootOf(from)?.PerformLayout();

    /// <summary>
    /// <see cref="BatchLayout"/> for the page (or dialog) <paramref name="from"/> is on: changes that each ask for a
    /// <see cref="Relayout"/> (texts, collapsed lines) get one layout of the page at the end.
    /// </summary>
    public static IDisposable BatchRelayout(Control from) => BatchLayout(RootOf(from) ?? from);

    /// <summary>The outermost layout container up from <paramref name="from"/>: its page, dialog or window.</summary>
    private static Control? RootOf(Control? from)
    {
        Control? root = null;
        for (var c = from; c != null; c = c.Parent)
        {
            if (c is ScrollPage or ILayoutRoot) return c;
            if (c is LayoutPanel) root = c;
        }
        return root;
    }
}

/// <summary>Stops <see cref="LayoutPanel.Relayout"/> here (dialogs, windows).</summary>
internal interface ILayoutRoot
{
}

/// <summary>Children top to bottom, full width, with a gap between them; collapsed or empty ones take no space.</summary>
internal class StackPanel : LayoutPanel
{
    public int Gap { get; set; } = 12;
    public Padding Inset { get; set; } = Padding.Empty;

    /// <summary>Extra space above particular children (e.g. a section header), in 96-DPI units.</summary>
    public Dictionary<Control, int> GapBefore { get; } = new();

    protected override int Arrange(int width, bool apply)
    {
        int x = Dp(Inset.Left), y = Dp(Inset.Top), w = Math.Max(0, width - Dp(Inset.Horizontal));
        bool first = true;
        foreach (Control c in Controls)
        {
            int h = HeightFor(c, w);
            if (h <= 0)
            {
                if (apply && c.Height != 0 && !IsCollapsed(c)) Place(c, x, y, w, 0);
                continue;
            }
            if (!first) y += Dp(GapBefore.TryGetValue(c, out var g) ? g : Gap);
            if (apply) Place(c, x, y, w, h);
            y += h;
            first = false;
        }
        return y + Dp(Inset.Bottom);
    }
}

/// <summary>
/// Children left to right, vertically centred, <see cref="Gap"/> apart. Each takes its preferred width,
/// except <see cref="Fill"/>, which takes what is left; <see cref="FixedWidths"/> pins a child's width.
/// </summary>
internal class RowPanel : LayoutPanel
{
    public int Gap { get; set; } = 8;
    public Control? Fill { get; set; }
    public Dictionary<Control, int> FixedWidths { get; } = new();
    public int MinHeight { get; set; }

    /// <summary>
    /// Without <see cref="Fill"/>: when the children don't fit on one line, they go on to the next (each at its own width)
    /// instead of the widest giving way. For a few buttons with long texts (German) at the window's minimum width.
    /// </summary>
    public bool Wrap { get; set; }

    private int WidthOf(Control c) => FixedWidths.TryGetValue(c, out var w) ? Dp(w) : WidthFor(c);

    /// <summary>With no width proposed: the natural width (every child at its preferred or fixed width).</summary>
    public override Size GetPreferredSize(Size proposedSize)
    {
        if (proposedSize.Width > 0) return base.GetPreferredSize(proposedSize);
        var items = Controls.Cast<Control>().Where(c => !IsCollapsed(c)).ToList();
        if (items.Count == 0) return Size.Empty;
        int width = items.Sum(WidthOf) + Dp(Gap) * (items.Count - 1);
        return new Size(width, Arrange(width, apply: false));
    }

    protected override int Arrange(int width, bool apply)
    {
        var items = Controls.Cast<Control>().Where(c => !IsCollapsed(c)).ToList();
        if (items.Count == 0) return 0;
        int gaps = Dp(Gap) * (items.Count - 1);
        int fixedSum = items.Where(c => c != Fill).Sum(WidthOf);
        int fillWidth = Math.Max(0, width - gaps - fixedSum);
        var widths = items.Select(c => c == Fill ? fillWidth : WidthOf(c)).ToList();
        int over = widths.Sum() + gaps - width;
        if (Fill == null && over > 0 && Wrap) return ArrangeLines(items, widths, width, apply);
        if (Fill == null && over > 0)
        {
            // Too narrow (the window's minimum width, long words): the widest child gives way instead of the last ones being
            // cut off (German 「Löschen」 after the VLC password, English 「Send to」; found by the GUI smoke test).
            int widest = widths.IndexOf(widths.Max());
            widths[widest] = Math.Max(0, widths[widest] - over);
        }
        var heights = items.Select((c, i) => HeightFor(c, widths[i])).ToList();
        int height = Math.Max(Dp(MinHeight), heights.Max());
        if (apply)
        {
            int x = 0;
            for (int i = 0; i < items.Count; i++)
            {
                Place(items[i], x, (height - heights[i]) / 2, widths[i], heights[i]);
                x += widths[i] + Dp(Gap);
            }
        }
        return height;
    }

    /// <summary><see cref="Wrap"/>: as many children per line as fit, lines <see cref="Gap"/> apart, each vertically centred.</summary>
    private int ArrangeLines(List<Control> items, List<int> widths, int width, bool apply)
    {
        int gap = Dp(Gap);
        var lines = new List<List<int>> { new() };
        int x = 0;
        for (int i = 0; i < items.Count; i++)
        {
            widths[i] = Math.Min(widths[i], width); // one child wider than the line has it to itself
            if (lines[^1].Count > 0 && x + widths[i] > width)
            {
                lines.Add([]);
                x = 0;
            }
            lines[^1].Add(i);
            x += widths[i] + gap;
        }
        var heights = items.Select((c, i) => HeightFor(c, widths[i])).ToList();
        int y = 0;
        foreach (var line in lines)
        {
            int lineHeight = Math.Max(Dp(MinHeight), line.Max(i => heights[i]));
            if (apply)
            {
                x = 0;
                foreach (int i in line)
                {
                    Place(items[i], x, y + (lineHeight - heights[i]) / 2, widths[i], heights[i]);
                    x += widths[i] + gap;
                }
            }
            y += lineHeight + gap;
        }
        return y - gap;
    }
}

/// <summary>A rounded card with a hairline border; lays its children out like a <see cref="StackPanel"/>.</summary>
internal class Card : LayoutPanel, ISurface
{
    public Card() => Padding = new Padding(16, 14, 16, 14);

    public virtual Color SurfaceColor => Theme.P.Card;
    public int Gap { get; set; } = 10;
    public int Radius { get; set; } = 8;

    /// <summary>Inner padding in 96-DPI units (the Padding property itself is not used for layout).</summary>
    public new Padding Padding { get; set; }

    protected Rectangle Inner(int width) =>
        new(Dp(Padding.Left), Dp(Padding.Top), Math.Max(0, width - Dp(Padding.Horizontal)), 0);

    protected override int Arrange(int width, bool apply)
    {
        var inner = Inner(width);
        int y = inner.Y;
        bool first = true;
        foreach (Control c in Controls)
        {
            int h = HeightFor(c, inner.Width);
            if (h <= 0)
            {
                // Out of the way, as StackPanel does: never placed, a panel kept its default 200x100 at the card's corner
                // and painted over it (麦克风与音效: the row of the collapsed 打开麦克风隐私设置 link, found by the GUI smoke test).
                if (apply && c.Height != 0 && !IsCollapsed(c)) Place(c, inner.X, y, inner.Width, 0);
                continue;
            }
            if (!first) y += Dp(Gap);
            if (apply) Place(c, inner.X, y, inner.Width, h);
            y += h;
            first = false;
        }
        return y + Dp(Padding.Bottom);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(FluentControl.SurfaceOf(Parent));
        int hair = Math.Max(1, DeviceDpi / 96);
        Shapes.Card(g, new Rectangle(0, 0, Width, Height), Dp(Radius), SurfaceColor, P.CardBorder, hair);
    }
}

/// <summary>
/// A page: vertical scrolling (system scroll bar in the theme's style) around a <see cref="StackPanel"/>
/// whose width is capped for readability. The scroll bar's width is always reserved so the layout never jumps.
/// </summary>
internal class ScrollPage : Panel, ISurface
{
    private bool _fontPending;

    public ScrollPage(string? title = null)
    {
        Content = new StackPanel { Inset = new Padding(28, 18, 20, 28), Gap = 12 };
        Controls.Add(Content);
        AutoScroll = true;
        DoubleBuffered = true;
        if (title != null)
        {
            TitleText = new TextBlock(title, TextStyle.Title);
            Content.Controls.Add(TitleText);
        }
        Theme.Changed += ThemeChanged;
        BackColor = Theme.P.Window;
    }

    public TextBlock? TitleText { get; }

    /// <summary>The page became the visible one (window shown and this page selected): start timers, refresh.</summary>
    public virtual void PageShown() { }

    /// <summary>The page is no longer visible: stop timers, let go of what it holds.</summary>
    public virtual void PageHidden() { }

    /// <summary>
    /// Build now, on the hidden page, what <see cref="PageShown"/> would build from the app's state, so that the first visit
    /// finds it (MainWindow prepares the pages at idle). Never a timer, a read or anything else only a shown page does.
    /// </summary>
    public virtual void Prepare() { }

    public StackPanel Content { get; }
    public int MaxContentWidth { get; set; } = 1040;
    public Color SurfaceColor => Theme.P.Window;

    /// <summary>The DPI of the page's last layout (0: never laid out).</summary>
    internal int LayoutDpi { get; private set; }

    private void ThemeChanged()
    {
        if (IsDisposed) return;
        BackColor = Theme.P.Window;
        Theme.StyleScrollBars(this);
        Invalidate(true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.StyleScrollBars(this);
    }

    /// <summary>
    /// The window's font for a new DPI reaches a hidden page's controls when the page is shown or prepared, not at once: every
    /// control's font changed and every window got a new font handle (WM_SETFONT), on every page, for each move to a monitor
    /// with another scale. 2026-10-15, all 11 pages visited: 636 font changes and 575 new GDI fonts per DPI change.
    /// </summary>
    protected override void OnParentFontChanged(EventArgs e)
    {
        if (Visible || Parent == null) base.OnParentFontChanged(e);
        else _fontPending = true;
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        if (Visible) ApplyFont();
        base.OnVisibleChanged(e);
    }

    /// <summary>
    /// The hidden page's windows destroyed (its controls stay, built and with what they hold): MainWindow before a move to a
    /// monitor with another scale, and its idle preparation creates them again at the new DPI.
    /// </summary>
    internal void ReleaseWindows()
    {
        if (!Visible && IsHandleCreated) DestroyHandle();
    }

    /// <summary>The font change the hidden page left for later (<see cref="OnParentFontChanged"/>), with one layout.</summary>
    internal void ApplyFont()
    {
        if (!_fontPending) return;
        _fontPending = false;
        using (LayoutPanel.BatchLayout(this)) base.OnParentFontChanged(EventArgs.Empty);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        if (Content is null) return; // AutoScroll lays out during construction
        LayoutPanel.Arranged?.Invoke(this);
        LayoutDpi = DeviceDpi;
        int scrollBar = SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi);
        int width = Math.Max(0, Math.Min(Width - scrollBar, Theme.Dp(MaxContentWidth, DeviceDpi)));
        Content.Inset = Width < Theme.Dp(640, DeviceDpi)
            ? new Padding(16, 18, 16, 28) : new Padding(28, 18, 20, 28);
        int height = Content.GetPreferredSize(new Size(width, 0)).Height;
        if (AutoScrollMinSize.Height != height) AutoScrollMinSize = new Size(0, height);
        var pos = AutoScrollPosition;
        int left = Math.Max(0, (Width - scrollBar - width) / 2);
        LayoutPanel.Place(Content, left + pos.X, pos.Y, width, height);
        base.OnLayout(levent);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= ThemeChanged;
        base.Dispose(disposing);
    }
}
