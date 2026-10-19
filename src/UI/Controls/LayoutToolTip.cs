namespace HomePodCast.UI.Controls;

/// <summary>Normal WinForms tooltips, plus hit-tested regions on a parent-painted layout surface.</summary>
internal sealed class LayoutToolTip : ToolTip
{
    private readonly HashSet<Control> _painted = [];
    private readonly HashSet<Control> _watched = [], _hosts = [];
    private readonly System.Windows.Forms.Timer _delay = new();
    private Control? _pending, _pendingHost;
    private bool _disposed;

    internal LayoutToolTip() => _delay.Tick += ShowPending;

    public new void SetToolTip(Control control, string? caption)
    {
        // Keep the extender's data as well: GetToolTip and a later native fallback still work normally.
        base.SetToolTip(control, caption);
        if (control is not PaintedControl) return;
        if (string.IsNullOrEmpty(caption)) _painted.Remove(control);
        else _painted.Add(control);
        Rebind();
    }

    private static Control? HostOf(Control control)
    {
        for (var parent = control.Parent; parent != null; parent = parent.Parent)
            if (!ParentPainting.IsPainted(parent)) return parent;
        return null;
    }

    // The regions can move, scroll, be clipped or acquire a native parent after a dynamic input is added.
    // Compute from the current tree rather than caching rectangles at the first page visit.
    internal Control? HintAt(Control host, Point screen) => _painted.FirstOrDefault(control =>
        !control.IsDisposed && ParentPainting.IsPainted(control) && HostOf(control) == host &&
        ParentPainting.ScreenBounds(control, clipped: true).Contains(screen));

    private void TreeChanged(object? sender, EventArgs e) => Rebind();

    private void Rebind()
    {
        if (_disposed) return;
        CancelPending();
        foreach (var control in _watched)
        {
            control.ParentChanged -= TreeChanged;
            control.Disposed -= TreeChanged;
            control.VisibleChanged -= TreeChanged;
            control.HandleCreated -= TreeChanged;
        }
        _watched.Clear();
        foreach (var host in _hosts) UnwatchHost(host);
        _hosts.Clear();
        _painted.RemoveWhere(control => control.IsDisposed);
        foreach (var control in _painted)
        {
            for (var current = control; current != null; current = current.Parent)
                if (_watched.Add(current))
                {
                    current.ParentChanged += TreeChanged;
                    current.Disposed += TreeChanged;
                    current.VisibleChanged += TreeChanged;
                    current.HandleCreated += TreeChanged;
                }
            if (HostOf(control) is { } host && _hosts.Add(host))
            {
                host.MouseMove += MouseMoved;
                host.MouseLeave += MouseLeft;
                host.MouseDown += MousePressed;
                host.VisibleChanged += HostVisibilityChanged;
                host.HandleDestroyed += MouseLeft;
            }
        }
    }

    private void MouseMoved(object? sender, MouseEventArgs e)
    {
        if (sender is not Control host || !host.IsHandleCreated) return;
        var hint = HintAt(host, host.PointToScreen(e.Location));
        if (hint == _pending && host == _pendingHost) return;
        CancelPending();
        if (hint == null) return;
        _pending = hint;
        _pendingHost = host;
        _delay.Interval = Math.Max(1, InitialDelay);
        _delay.Start();
    }

    private void MouseLeft(object? sender, EventArgs e) => CancelPending();
    private void MousePressed(object? sender, MouseEventArgs e) => CancelPending();
    private void HostVisibilityChanged(object? sender, EventArgs e)
    {
        if (sender is Control { Visible: false }) CancelPending();
    }

    private void ShowPending(object? sender, EventArgs e)
    {
        _delay.Stop();
        if (!Active || _pending is not { } control || _pendingHost is not { IsHandleCreated: true } host
            || host.FindForm() is { ContainsFocus: false } || HintAt(host, Cursor.Position) != control) return;
        var point = host.PointToClient(Cursor.Position);
        point.Offset(Theme.Dp(12, host.DeviceDpi), Theme.Dp(20, host.DeviceDpi));
        Show(GetToolTip(control), host, point, AutoPopDelay);
    }

    private void CancelPending()
    {
        _delay.Stop();
        if (_pendingHost is { IsDisposed: false, IsHandleCreated: true } host) Hide(host);
        _pending = _pendingHost = null;
    }

    private void UnwatchHost(Control host)
    {
        host.MouseMove -= MouseMoved;
        host.MouseLeave -= MouseLeft;
        host.MouseDown -= MousePressed;
        host.VisibleChanged -= HostVisibilityChanged;
        host.HandleDestroyed -= MouseLeft;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            CancelPending();
            _delay.Dispose();
            foreach (var control in _watched)
            {
                control.ParentChanged -= TreeChanged;
                control.Disposed -= TreeChanged;
                control.VisibleChanged -= TreeChanged;
                control.HandleCreated -= TreeChanged;
            }
            foreach (var host in _hosts) UnwatchHost(host);
            _painted.Clear(); _watched.Clear(); _hosts.Clear();
        }
        base.Dispose(disposing);
    }
}
