namespace HomePodCast.UI.Controls;

internal abstract partial class LayoutPanel
{
    private bool _parentPainted, _shown = true;
    internal bool ParentPainted => _parentPainted;
    bool IParentPainted.ParentPainted => ParentPainted;
    bool IParentPainted.Shown => _shown;

    // By default an input keeps its native ancestors. An opted-in LogicalLayout instead parents the inputs to its
    // surface; the physical children left in these rows/cards are painted, while their logical children stay intact.
    internal void UpdateParentPainting()
    {
        bool painted = Parent is LayoutPanel && !IsHandleCreated &&
            Controls.Cast<Control>().All(ParentPainting.IsPainted);
        if (painted == _parentPainted) return;
        _parentPainted = painted;
        _changingParentPainting = true;
        try { base.SetVisibleCore(painted ? false : _shown); }
        finally { _changingParentPainting = false; }
        ParentPainting.Invalidate(Parent, Bounds);
        ParentPainting.Notify(this, AccessibleEvents.Reorder);
        (Parent as LayoutPanel)?.UpdateParentPainting();
    }

    private bool _changingParentPainting;
    protected override void OnVisibleChanged(EventArgs e)
    {
        if (_changingParentPainting && !IsHandleCreated)
        {
            bool parentHasWindow = false;
            for (var parent = Parent; parent != null; parent = parent.Parent)
                parentHasWindow |= parent.IsHandleCreated;
            // Restoring the native visibility bit while constructing a detached subtree must not eagerly
            // create its windows. The eventual host creates them after layout, or when the subtree is shown.
            if (!parentHasWindow) return;
        }
        base.OnVisibleChanged(e);
        LogicalLayout.Changed(this);
    }

    public new bool Visible { get => ParentPainting.IsShown(this); set => SetVisibleCore(value); }

    public new void Invalidate() => Invalidate(ClientRectangle);
    public new void Invalidate(Rectangle bounds)
    {
        if (!ParentPainted) { base.Invalidate(bounds); return; }
        bounds.Offset(Location);
        ParentPainting.Invalidate(Parent, bounds);
    }

    protected override void OnParentChanged(EventArgs e)
    {
        UpdateParentPainting();
        base.OnParentChanged(e);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        UpdateParentPainting(); // an explicit native-service request ends parent painting as well
        base.OnHandleCreated(e);
    }

    protected override void SetVisibleCore(bool value)
    {
        bool changed = _shown != value;
        _shown = value;
        if (!ParentPainted) base.SetVisibleCore(value);
        else if (changed)
        {
            OnVisibleChanged(EventArgs.Empty);
            ParentPainting.Invalidate(Parent, Bounds);
            ParentPainting.Notify(this, AccessibleEvents.Reorder);
        }
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

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        LogicalLayout.Changed(this);
        if (!ParentPainted) return;
        ParentPainting.Invalidate(Parent, Bounds);
        ParentPainting.Notify(this, AccessibleEvents.StateChange);
    }

    void IParentPainted.PaintInParent(PaintEventArgs e) { OnPaintBackground(e); OnPaint(e); }

    // WinForms updates an existing ControlAccessibleObject when a HWND is created. A display-only panel can
    // acquire one after a dynamic input arrives, so its cached accessible object must support that transition.
    private sealed class PaintedPanelAccessible(LayoutPanel owner) : ControlAccessibleObject(owner)
    {
        private Control[] Children => LogicalLayout.Children(owner)
            .Where(c => IsShown(c) && !IsCollapsed(c)).ToArray();
        public override string? Name { get => owner.AccessibleName ?? owner.Text; set => owner.AccessibleName = value; }
        public override string? Description => owner.AccessibleDescription;
        public override AccessibleRole Role => owner.AccessibleRole == AccessibleRole.Default ? AccessibleRole.Pane : owner.AccessibleRole;
        public override AccessibleObject? Parent => owner.Parent?.AccessibilityObject;
        public override Rectangle Bounds => ParentPainting.ScreenBounds(owner);
        public override AccessibleStates State => (owner.Enabled ? AccessibleStates.None : AccessibleStates.Unavailable)
            | (IsShown(owner) && !owner.Collapsed ? AccessibleStates.None : AccessibleStates.Invisible)
            | (ParentPainting.ScreenBounds(owner, clipped: true).IsEmpty ? AccessibleStates.Offscreen : AccessibleStates.None);
        public override int GetChildCount() => Children.Length;
        public override AccessibleObject? GetChild(int index)
        {
            var children = Children;
            return index >= 0 && index < children.Length ? children[index].AccessibilityObject : null;
        }
        public override AccessibleObject? HitTest(int x, int y)
        {
            if (!ParentPainting.ScreenBounds(owner, clipped: true).Contains(x, y)) return null;
            foreach (var child in Children)
                if (child.AccessibilityObject.HitTest(x, y) is { } hit) return hit;
            return this;
        }
    }
}
