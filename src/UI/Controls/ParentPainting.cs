using System.Runtime.InteropServices;

namespace HomePodCast.UI.Controls;

/// <summary>A display-only item whose layout parent supplies its pixels and accessibility node.</summary>
internal interface IParentPainted
{
    bool ParentPainted { get; }
    bool Shown { get; }
    void PaintInParent(PaintEventArgs e);
}

internal static class ParentPainting
{
    internal static bool IsPainted(Control c) => c is IParentPainted { ParentPainted: true };

    internal static bool IsShown(Control? c) => c == null ||
        (c is IParentPainted { ParentPainted: true } painted ? painted.Shown && IsShown(c.Parent) : c.Visible);

    internal static int Dpi(Control c) => IsPainted(c) && c.Parent != null ? Dpi(c.Parent) : c.DeviceDpi;

    internal static void Invalidate(Control? parent, Rectangle bounds)
    {
        while (parent != null && IsPainted(parent))
        {
            bounds.Offset(parent.Location);
            parent = parent.Parent;
        }
        parent?.Invalidate(bounds);
    }

    internal static Rectangle ScreenBounds(Control c, bool clipped = false)
    {
        var bounds = c.ClientRectangle;
        for (Control? current = c; current != null; current = current.Parent)
        {
            if (clipped)
            {
                if (!IsShown(current) || LayoutPanel.IsCollapsed(current)) return Rectangle.Empty;
                bounds.Intersect(current.ClientRectangle);
            }
            if (!IsPainted(current))
            {
                if (!current.IsHandleCreated) return Rectangle.Empty;
                bounds = current.RectangleToScreen(bounds);
                if (clipped)
                    for (var parent = current.Parent; parent != null; parent = parent.Parent)
                    {
                        if (!IsShown(parent) || !parent.IsHandleCreated) return Rectangle.Empty;
                        bounds.Intersect(parent.RectangleToScreen(parent.ClientRectangle));
                    }
                return bounds;
            }
            bounds.Offset(current.Location);
        }
        return Rectangle.Empty;
    }

    internal static void Draw(Control c, Graphics graphics)
    {
        if (c is not IParentPainted { ParentPainted: true } painted || !IsShown(c) || LayoutPanel.IsCollapsed(c)
            || c.Width <= 0 || c.Height <= 0 || !graphics.IsVisible(c.Bounds)) return;
        // Use a translated device context, as a native child paint does. A Graphics world transform alone
        // is insufficient for nested GDI TextRenderer calls mixed with GDI+ icons and backgrounds.
        var dc = graphics.GetHdc();
        int saved = SaveDC(dc);
        try
        {
            OffsetViewportOrgEx(dc, c.Left, c.Top, IntPtr.Zero);
            IntersectClipRect(dc, 0, 0, c.Width, c.Height);
            using var childGraphics = Graphics.FromHdc(dc);
            using var e = new PaintEventArgs(childGraphics, Rectangle.Intersect(c.ClientRectangle,
                Rectangle.Ceiling(childGraphics.VisibleClipBounds)));
            painted.PaintInParent(e);
        }
        finally
        {
            RestoreDC(dc, saved);
            graphics.ReleaseHdc(dc);
        }
    }

    [DllImport("gdi32.dll")] private static extern int SaveDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool RestoreDC(IntPtr dc, int saved);
    [DllImport("gdi32.dll")] private static extern bool OffsetViewportOrgEx(IntPtr dc, int x, int y, IntPtr previous);
    [DllImport("gdi32.dll")] private static extern int IntersectClipRect(IntPtr dc, int left, int top, int right, int bottom);

    internal static void Notify(Control owner, AccessibleEvents change)
    {
        for (var c = owner.Parent; c is LayoutPanel panel; c = c.Parent)
        {
            if (IsPainted(panel)) continue;
            panel.PaintedElementChanged(owner, change);
            break;
        }
    }
}

/// <summary>Noninteractive visuals keep managed layout/binding, but need no child HWND on a layout surface.</summary>
internal abstract class PaintedControl : FluentControl, IParentPainted
{
    private readonly bool _nativeWindow;
    private bool _shown = true;
    internal bool ParentPainted { get; private set; }
    bool IParentPainted.ParentPainted => ParentPainted;
    bool IParentPainted.Shown => _shown;

    protected PaintedControl(bool nativeWindow = false)
    {
        AccessibleRole = AccessibleRole.Graphic;
        _nativeWindow = nativeWindow;
        ParentPainted = !nativeWindow;
        if (ParentPainted) base.SetVisibleCore(false);
    }

    public new bool Visible { get => ParentPainting.IsShown(this); set => SetVisibleCore(value); }

    protected override void OnParentChanged(EventArgs e)
    {
        if (Parent != null)
        {
            ParentPainted = !_nativeWindow && Parent is LayoutPanel && !IsHandleCreated;
            base.SetVisibleCore(ParentPainted ? false : _shown);
        }
        base.OnParentChanged(e);
    }

    protected override void SetVisibleCore(bool value)
    {
        bool changed = _shown != value;
        _shown = value;
        if (!ParentPainted) base.SetVisibleCore(value);
        else if (changed)
        {
            OnVisibleChanged(EventArgs.Empty);
            Repaint();
            ParentPainting.Notify(this, AccessibleEvents.Reorder);
        }
    }

    protected void Repaint()
    {
        if (ParentPainted) ParentPainting.Invalidate(Parent, Bounds);
        else Invalidate();
    }

    protected override void OnThemeChanged() => Repaint();
    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Repaint();
        if (ParentPainted) ParentPainting.Notify(this, AccessibleEvents.StateChange);
    }
    protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
    {
        var old = Bounds;
        base.SetBoundsCore(x, y, width, height, specified);
        if (ParentPainted && old != Bounds)
        {
            ParentPainting.Invalidate(Parent, Rectangle.Union(old, Bounds));
            ParentPainting.Notify(this, AccessibleEvents.LocationChange);
        }
    }
    void IParentPainted.PaintInParent(PaintEventArgs e) { OnPaintBackground(e); OnPaint(e); }

    protected override AccessibleObject CreateAccessibilityInstance() => ParentPainted
        ? new PaintedAccessible(this) : base.CreateAccessibilityInstance();

    private sealed class PaintedAccessible(PaintedControl owner) : AccessibleObject
    {
        public override string? Name { get => owner.AccessibleName ?? owner.Text; set => owner.AccessibleName = value; }
        public override string? Description => owner.AccessibleDescription;
        public override string? Value => owner is MetricTile metric ? metric.ValueText : owner.Text;
        public override AccessibleRole Role => owner.AccessibleRole;
        public override AccessibleObject? Parent => owner.Parent?.AccessibilityObject;
        public override Rectangle Bounds => ParentPainting.ScreenBounds(owner);
        public override AccessibleStates State => (owner.Enabled ? AccessibleStates.None : AccessibleStates.Unavailable)
            | (owner.Visible && !owner.Collapsed ? AccessibleStates.None : AccessibleStates.Invisible)
            | (ParentPainting.ScreenBounds(owner, clipped: true).IsEmpty ? AccessibleStates.Offscreen : AccessibleStates.None);
        public override int GetChildCount() => 0;
        public override AccessibleObject? HitTest(int x, int y) => ParentPainting.ScreenBounds(owner, clipped: true).Contains(x, y) ? this : null;
        public override AccessibleObject? Navigate(AccessibleNavigation direction)
        {
            if (Parent is not { } parent) return null;
            for (int i = 0; i < parent.GetChildCount(); i++)
                if (ReferenceEquals(parent.GetChild(i), this))
                    return direction switch
                    {
                        AccessibleNavigation.Next => parent.GetChild(i + 1),
                        AccessibleNavigation.Previous => parent.GetChild(i - 1),
                        _ => null,
                    };
            return null;
        }
    }
}
