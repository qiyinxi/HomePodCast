using System.Drawing.Drawing2D;

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

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        RequestLayout();
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
    protected int LineHeight(TextStyle style) => TextRenderer.MeasureText("Ag国", StyleFont(style), Size.Empty, TextFlags.Measure).Height;
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

    protected override void OnLayout(LayoutEventArgs levent)
    {
        // Not while a subclass is still being built (Controls.Add lays out): only once it is on a parent.
        if (Width > 0 && Parent != null) Arrange(Width, apply: true);
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

    public static bool IsCollapsed(Control c) => c is FluentControl { Collapsed: true } or LayoutPanel { Collapsed: true };

    /// <summary>Height a child wants at <paramref name="width"/> (0 when collapsed).</summary>
    public static int HeightFor(Control c, int width) => IsCollapsed(c) ? 0 : c.GetPreferredSize(new Size(width, 0)).Height;

    public static int WidthFor(Control c) => IsCollapsed(c) ? 0 : c.GetPreferredSize(Size.Empty).Width;

    /// <summary>Set bounds (only when they changed) and lay a container out again.</summary>
    public static void Place(Control c, int x, int y, int w, int h)
    {
        if (c.Left != x || c.Top != y || c.Width != w || c.Height != h) c.SetBounds(x, y, Math.Max(0, w), Math.Max(0, h));
        if (c is LayoutPanel) c.PerformLayout();
    }

    /// <summary>Measure and position everything again, from the outermost layout container up from <paramref name="from"/>.</summary>
    public static void Relayout(Control? from)
    {
        Control? root = null;
        for (var c = from; c != null; c = c.Parent)
        {
            if (c is ScrollPage or ILayoutRoot)
            {
                root = c;
                break;
            }
            if (c is LayoutPanel) root = c;
        }
        root?.PerformLayout();
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
            if (h <= 0) continue;
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

    public StackPanel Content { get; }
    public int MaxContentWidth { get; set; } = 1040;
    public Color SurfaceColor => Theme.P.Window;

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

    protected override void OnLayout(LayoutEventArgs levent)
    {
        if (Content is null) return; // AutoScroll lays out during construction
        int scrollBar = SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi);
        int width = Math.Max(0, Math.Min(Width - scrollBar, Theme.Dp(MaxContentWidth, DeviceDpi)));
        int height = Content.GetPreferredSize(new Size(width, 0)).Height;
        if (AutoScrollMinSize.Height != height) AutoScrollMinSize = new Size(0, height);
        var pos = AutoScrollPosition;
        LayoutPanel.Place(Content, pos.X, pos.Y, width, height);
        base.OnLayout(levent);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        PerformLayout();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= ThemeChanged;
        base.Dispose(disposing);
    }
}
