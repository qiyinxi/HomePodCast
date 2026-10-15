using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace HomePodCast.UI.Controls;

/// <summary>Two client snapshots, one theme update. Animation frames never measure or lay out controls.</summary>
internal sealed class ThemeTransition : Control
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private Bitmap? _before, _after;
    private long _started;
    internal bool Running => _timer.Enabled;

    public ThemeTransition()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        TabStop = false;
        Visible = false;
        AccessibleRole = AccessibleRole.None;
        _timer.Tick += (_, _) => Advance();
    }

    public void Run(Form window, Action change)
    {
        Stop();
        bool animate = window.Visible && window.Opacity > 0 && AnimationsEnabled &&
            window.WindowState != FormWindowState.Minimized && window.ClientSize.Width > 0 && window.ClientSize.Height > 0;
        if (animate) _before = Snapshot(window);
        using (LayoutPanel.BatchLayout(window)) change();
        if (_before == null) return;
        _after = Snapshot(window);
        if (_after == null) { Stop(); return; }
        Bounds = window.ClientRectangle;
        BringToFront();
        _started = Environment.TickCount64;
        Visible = true;
        _timer.Start();
    }

    private static bool AnimationsEnabled => SystemParametersInfo(0x1042, 0, out int enabled, 0) && enabled != 0; // SPI_GETCLIENTAREAANIMATION
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, out int value, uint flags);

    private static Bitmap? Snapshot(Form window)
    {
        try
        {
            using var full = new Bitmap(window.Width, window.Height);
            window.DrawToBitmap(full, new Rectangle(Point.Empty, window.Size));
            var origin = window.PointToScreen(Point.Empty) - (Size)window.Location;
            var area = new Rectangle(origin, window.ClientSize);
            return full.Clone(Rectangle.Intersect(new Rectangle(Point.Empty, full.Size), area), PixelFormat.Format32bppPArgb);
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or InvalidOperationException)
        {
            return null; // Low resources or a window disappearing: the theme still changes immediately.
        }
    }

    internal void Advance()
    {
        if (Environment.TickCount64 - _started >= 180) Stop();
        else Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_before == null || _after == null) return;
        e.Graphics.DrawImageUnscaled(_after, Point.Empty);
        float alpha = 1 - Math.Clamp((Environment.TickCount64 - _started) / 180f, 0, 1);
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = alpha });
        e.Graphics.DrawImage(_before, ClientRectangle, 0, 0, _before.Width, _before.Height, GraphicsUnit.Pixel, attributes);
    }

    public void Stop()
    {
        _timer.Stop();
        Visible = false;
        _before?.Dispose(); _before = null;
        _after?.Dispose(); _after = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Stop(); _timer.Dispose(); }
        base.Dispose(disposing);
    }
}
