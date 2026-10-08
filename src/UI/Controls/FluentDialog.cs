namespace HomePodCast.UI.Controls;

/// <summary>
/// A small modal dialog in the app's style: themed background and title bar, content in a
/// <see cref="StackPanel"/>, the window sized to the content for its DPI (no AutoScale).
/// </summary>
internal abstract class FluentDialog : Form, ISurface, ILayoutRoot
{
    private bool _fitting;

    protected FluentDialog(string title)
    {
        Theme.Watch();
        AutoScaleMode = AutoScaleMode.None;
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Icon = Icons.App;
        BackColor = Theme.P.Window;
        Font = Theme.Font(TextStyle.Body, DeviceDpi);
        Body = new StackPanel { Inset = new Padding(24, 20, 24, 20), Gap = 12 };
        Controls.Add(Body);
        Theme.Changed += ThemeChanged;
    }

    /// <summary>The dialog's content, top to bottom.</summary>
    public StackPanel Body { get; }

    /// <summary>Width of the window's client area in 96-DPI units.</summary>
    protected int ContentWidth { get; set; } = 480;

    public virtual Color SurfaceColor => Theme.P.Window;

    protected int Dp(float value) => Theme.Dp(value, DeviceDpi);

    /// <summary>Buttons at the bottom right.</summary>
    protected static RowPanel ButtonRow(params Control[] buttons)
    {
        var spacer = new TextBlock();
        return Ui.Row(8, spacer, [spacer, .. buttons]);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        if (_fitting || Body is null) return; // Form setters lay out before the constructor is done
        _fitting = true;
        try
        {
            int w = Dp(ContentWidth);
            int h = Body.GetPreferredSize(new Size(w, 0)).Height;
            if (ClientSize != new Size(w, h)) ClientSize = new Size(w, h);
            LayoutPanel.Place(Body, 0, 0, w, h);
        }
        finally
        {
            _fitting = false;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.StyleWindow(this);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        PerformLayout();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Font = Theme.Font(TextStyle.Body, e.DeviceDpiNew);
        PerformLayout();
    }

    private void ThemeChanged()
    {
        if (IsDisposed) return;
        BackColor = Theme.P.Window;
        Theme.StyleWindow(this);
        OnThemeChanged();
        Invalidate(true);
    }

    /// <summary>Recolour standard controls that do not follow the theme by themselves.</summary>
    protected virtual void OnThemeChanged() { }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= ThemeChanged;
        base.Dispose(disposing);
    }
}
