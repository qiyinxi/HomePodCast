using System.Runtime.CompilerServices;

namespace HomePodCast.UI.Controls;

/// <summary>
/// A layout surface owns two trees: rows/cards retain their logical children, while input controls are real children of
/// the surface. Construct before creating windows; no temporary row HWNDs, hidden parent windows or native SetParent hacks.
/// </summary>
internal sealed class LogicalLayout
{
    private static readonly ConditionalWeakTable<Control, Node> Nodes = new();
    private readonly LayoutPanel _host;
    private int _changing;
    private bool _applying, _disposed;

    private sealed class Node(LogicalLayout tree, Control control, Control? parent)
    {
        internal readonly LogicalLayout Tree = tree;
        internal readonly Control? Parent = parent;
        internal readonly List<Control> Children = [];
        internal Rectangle Bounds = control.Bounds;
        internal int TabIndex = control.TabIndex;
        internal bool Shown = control is FluentControl fluent ? fluent.RequestedVisible : control.Visible;
        internal bool Enabled = control is FluentControl fluent ? fluent.RequestedEnabled : control.Enabled;
        internal bool Input;
    }

    private LogicalLayout(LayoutPanel host)
    {
        _host = host;
        _host.SuspendLayout();
        _changing++;
        try { Attach(host, null); }
        finally { _changing--; _host.ResumeLayout(false); }
        Refresh();
    }

    internal static void Enable(LayoutPanel host)
    {
        if (Nodes.TryGetValue(host, out _)) return;
        if (host.IsHandleCreated) throw new InvalidOperationException("Enable logical layout before creating the surface HWND.");
        Validate(host);
        _ = new LogicalLayout(host);
    }

    private static void Validate(Control control)
    {
        if (control is LayoutPanel)
        {
            foreach (Control child in control.Controls) Validate(child);
        }
        else if (!ParentPainting.IsPainted(control) && control is not FluentControl)
            throw new InvalidOperationException($"{control.GetType().Name} needs a native host; do not flatten that subtree.");
    }

    internal static Control? Parent(Control control) => Nodes.TryGetValue(control, out var node) ? node.Parent ?? control.Parent : control.Parent;
    internal static IEnumerable<Control> Children(Control control) => Nodes.TryGetValue(control, out var node) && !node.Input
        ? node.Children.ToArray() : control.Controls.Cast<Control>();
    internal static Rectangle Bounds(Control control) => Nodes.TryGetValue(control, out var node) && node.Input ? node.Bounds : control.Bounds;
    internal static bool IsHost(Control control) => Nodes.TryGetValue(control, out var node) && node.Tree._host == control;
    internal static bool IsInput(Control control) => Nodes.TryGetValue(control, out var node) && node.Input;
    internal static LayoutPanel? Host(Control control) => Nodes.TryGetValue(control, out var node) ? node.Tree._host : null;
    internal static bool BuildingDetached(Control control) => Nodes.TryGetValue(control, out var node)
        && node.Tree._changing != 0 && !node.Tree._host.IsHandleCreated;

    private void Attach(Control control, Control? parent)
    {
        var node = new Node(this, control, parent);
        Nodes.Add(control, node);
        control.TabIndexChanged += InputTabIndexChanged;
        if (control is LayoutPanel)
        {
            var children = control.Controls.Cast<Control>().ToArray();
            node.Children.AddRange(children);
            foreach (var child in children) Attach(child, control);
        }
        else if (!ParentPainting.IsPainted(control))
        {
            if (control is not FluentControl)
                throw new InvalidOperationException($"{control.GetType().Name} needs a native host; do not flatten that subtree.");
            node.Input = true;
            control.Parent = _host;
        }
    }

    internal static void Added(LayoutPanel parent, Control child)
    {
        if (!Nodes.TryGetValue(parent, out var node) || node.Tree._changing != 0) return;
        Validate(child);
        var tree = node.Tree;
        tree._host.SuspendLayout();
        tree._changing++;
        try { node.Children.Add(child); tree.Attach(child, parent); }
        finally { tree._changing--; tree._host.ResumeLayout(false); }
        tree.Refresh();
    }

    internal static void Removed(LayoutPanel parent, Control child)
    {
        if (!Nodes.TryGetValue(parent, out var node) || node.Tree._changing != 0 || node.Tree._disposed) return;
        if (!Nodes.TryGetValue(child, out var childNode)) return;
        // A moved input is a physical child of the host, but belongs to its original row.
        if (childNode.Parent != null && Nodes.TryGetValue(childNode.Parent, out var logicalParent))
            logicalParent.Children.Remove(child);
        node.Tree.Detach(child, disposeInputs: child.Disposing || child.IsDisposed);
        node.Tree.Refresh();
    }

    private void Detach(Control control, bool disposeInputs)
    {
        if (!Nodes.TryGetValue(control, out var node)) return;
        _host.SuspendLayout();
        _changing++;
        try
        {
            if (node.Parent != null && Nodes.TryGetValue(node.Parent, out var parent)) parent.Children.Remove(control);
            foreach (var child in node.Children.ToArray()) Detach(child, disposeInputs);
            control.TabIndexChanged -= InputTabIndexChanged;
            Nodes.Remove(control);
            if (node.Input && !control.IsDisposed && !control.Disposing)
            {
                if (disposeInputs) control.Dispose();
                else
                {
                    control.Parent = node.Parent;
                    control.Bounds = node.Bounds;
                    control.TabIndex = node.TabIndex;
                    ((FluentControl)control).ApplyLogicalState(node.Shown, node.Enabled);
                }
            }
        }
        finally { _changing--; _host.ResumeLayout(false); }
    }

    internal static void Disposing(Control panel)
    {
        if (!Nodes.TryGetValue(panel, out var node)) return;
        var tree = node.Tree;
        if (panel == tree._host) tree._disposed = true;
        tree.Detach(panel, disposeInputs: true);
    }

    private Point Offset(Control? parent)
    {
        var offset = Point.Empty;
        for (var current = parent; current != null && current != _host; current = Parent(current))
            offset.Offset(Bounds(current).Location);
        return offset;
    }

    internal static bool PlaceInput(Control control, Rectangle bounds)
    {
        if (!Nodes.TryGetValue(control, out var node) || !node.Input) return false;
        node.Bounds = bounds;
        bounds.Offset(node.Tree.Offset(node.Parent));
        node.Tree.PlaceNative(control, bounds);
        return true;
    }

    private void PlaceNative(Control control, Rectangle bounds)
    {
        if (control.Bounds == bounds) return;
        _host.SuspendLayout();
        try { control.Bounds = bounds; }
        finally { _host.ResumeLayout(false); }
    }

    internal static bool SetVisible(FluentControl control, bool value)
    {
        if (!Nodes.TryGetValue(control, out var node) || !node.Input || node.Tree._applying) return false;
        node.Shown = value;
        node.Tree.Refresh();
        return true;
    }

    internal static bool SetEnabled(FluentControl control, bool value)
    {
        if (!Nodes.TryGetValue(control, out var node) || !node.Input || node.Tree._applying) return false;
        node.Enabled = value;
        node.Tree.Refresh();
        return true;
    }

    internal static void Changed(Control control)
    {
        if (Nodes.TryGetValue(control, out var node) && node.Tree._changing == 0) node.Tree.Refresh();
    }

    private void InputTabIndexChanged(object? sender, EventArgs e)
    {
        if (_applying || sender is not Control control || !Nodes.TryGetValue(control, out var node)) return;
        node.TabIndex = control.TabIndex;
        Refresh();
    }

    private void Refresh()
    {
        if (_disposed || _applying || _changing != 0) return;
        _applying = true;
        try
        {
            int tabIndex = 0;
            void Visit(Control control, bool shown, bool enabled)
            {
                if (!Nodes.TryGetValue(control, out var node)) return;
                if (node.Input)
                {
                    var input = (FluentControl)control;
                    input.ApplyLogicalState(shown && node.Shown, enabled && node.Enabled);
                    input.TabIndex = tabIndex++;
                    var bounds = node.Bounds;
                    bounds.Offset(Offset(node.Parent));
                    PlaceNative(input, bounds);
                    return;
                }
                shown &= LayoutPanel.IsShown(control) && !LayoutPanel.IsCollapsed(control);
                enabled &= control.Enabled;
                foreach (var child in node.Children.OrderBy(child => Nodes.TryGetValue(child, out var entry) ? entry.TabIndex : child.TabIndex))
                    Visit(child, shown, enabled);
            }
            Visit(_host, true, true);
        }
        finally { _applying = false; }
    }
}
