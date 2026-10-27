using System.ComponentModel;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace HomePodCast.UI.Controls;

/// <summary>
/// A theme change faded in. A window of its own lies over the client area, showing the window as it was (one snapshot); the
/// theme changes and the window repaints completely underneath; only then does the clock start. Each frame only lowers the
/// cover's alpha, which DWM blends: nothing paints, measures or lays out. The cover hides at alpha 0, over a window that has
/// been final since the fade began, so the end changes nothing on screen. It lets the mouse through (layered, transparent,
/// never activated): hover, the cursor, tooltips and clicks stay with the controls under it.
/// 2026-10-27, the maintainer: in dark mode, after a click on 「浅色」, the bottom of the rail jumped. The cover was a child
/// control: its last frame was never alpha 0 (about 0.1), and hiding it uncovered the window, which repainted one window at a
/// time, the rail first, then the theme button, 设置 and 关于 over the stale frame. Its snapshots, taken in the click, also
/// showed the theme button pressed, and the overlay took the mouse from it.
/// </summary>
internal sealed class ThemeTransition : IDisposable
{
    internal const int DurationMs = 180;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private Cover? _cover;
    private Form? _window;
    private Bitmap? _image; // what the cover shows: the window before the change
    private long _started;

    public ThemeTransition()
    {
        _timer.Tick += (_, _) => Advance();
    }

    internal bool Running => _timer.Enabled;

    /// <summary>Tests: snapshots of the window taken (one per animated theme change, none for anything else).</summary>
    internal int Snapshots { get; private set; }

    /// <summary>The cover's alpha last shown (1: opaque, 0: the window alone).</summary>
    internal float Alpha { get; private set; }

    /// <summary>Tests: fade even in an invisible test window (Opacity 0) and without the system's animation setting.</summary>
    [ThreadStatic] internal static bool ForceAnimation;

    /// <summary>Tests: the clock started (the window has repainted in the new theme under the opaque cover).</summary>
    internal event Action? FadeStarted;

    /// <summary>Tests: the cover's window and the image it shows.</summary>
    internal IntPtr CoverHandle => _cover?.Handle ?? IntPtr.Zero;
    internal Bitmap? CoverImage => _image;

    /// <summary>Make <paramref name="change"/> (a theme change, laid out once) and fade the window into it when it is shown.</summary>
    public void Run(Form window, Action change)
    {
        var image = Animate(window) ? Capture(window) : null;
        if (image == null)
        {
            Stop();
            Apply(window, change);
            return;
        }
        Snapshots++;
        _timer.Stop();
        _image?.Dispose();
        _image = image;
        _window = window;
        if (!ShowCover(window, image))
        {
            Stop();
            Apply(window, change);
            return;
        }
        try
        {
            Apply(window, change);
        }
        catch
        {
            Stop();
            throw;
        }
        if (_image != image) return; // stopped meanwhile (moved, resized, hidden): the window shows as it is
        // Every window in it painted now, under the cover, rather than one by one while it fades: the fade then starts from
        // its first frame and ends on what the window shows (RDW_FRAME: the scroll bars in their new style too).
        RedrawWindow(window.Handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN | RDW_UPDATENOW);
        _started = Environment.TickCount64;
        _timer.Start();
        FadeStarted?.Invoke();
    }

    private static void Apply(Form window, Action change)
    {
        using (LayoutPanel.BatchLayout(window)) change();
    }

    private static bool Animate(Form window) =>
        window.Visible && window.IsHandleCreated && window.WindowState != FormWindowState.Minimized &&
        window.ClientSize.Width > 0 && window.ClientSize.Height > 0 &&
        (ForceAnimation || window.Opacity > 0 && AnimationsEnabled);

    private static bool AnimationsEnabled => SystemParametersInfo(0x1042, 0, out int enabled, 0) && enabled != 0; // SPI_GETCLIENTAREAANIMATION

    /// <summary>
    /// What the window shows now. While a fade runs, that is its cover over it: a second change (a double click on the theme
    /// button, Windows switching meanwhile) fades on from there instead of jumping back to a theme.
    /// </summary>
    private Bitmap? Capture(Form window)
    {
        var image = Snapshot(window);
        if (image == null || !Running || _image == null) return image;
        try
        {
            using var g = Graphics.FromImage(image);
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(new ColorMatrix { Matrix33 = Alpha });
            g.DrawImage(_image, new Rectangle(Point.Empty, image.Size), 0, 0, _image.Width, _image.Height, GraphicsUnit.Pixel, attributes);
            return image;
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or InvalidOperationException)
        {
            image.Dispose();
            return null;
        }
    }

    /// <summary>The client area as the window paints it (WM_PRINT); null when it can't be captured.</summary>
    internal static Bitmap? Snapshot(Form window)
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

    /// <summary>The cover, opaque, over the client area and right above the window; false when it can't show the image.</summary>
    private bool ShowCover(Form window, Bitmap image)
    {
        try
        {
            if (_cover == null || _cover.Handle == IntPtr.Zero || _cover.Owner != window.Handle)
            {
                _cover?.DestroyHandle();
                _cover = new Cover(window.Handle);
            }
            // Windows 11 rounds the window's bottom corners (8 px at 100 %, not when maximized): a square cover would show
            // the old theme in the corners beyond them for the length of the fade.
            int corner = Theme.RoundedWindows && window.WindowState == FormWindowState.Normal ? Theme.Dp(8, window.DeviceDpi) : 0;
            if (!_cover.Show(image, window.RectangleToScreen(window.ClientRectangle).Location, Level(1), corner)) return false;
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or Win32Exception)
        {
            return false;
        }
        Alpha = 1;
        // On screen before the window changes under it. DWM composes on its own clock: without this, a frame could show the
        // window repainting in the new theme before the cover with the old one.
        _ = DwmFlush();
        return true;
    }

    /// <summary>The cover's alpha: as transparent as the window it covers (tests: an invisible window, Opacity 0).</summary>
    private byte Level(float alpha) =>
        (byte)Math.Round(255 * Math.Clamp(alpha, 0, 1) * Math.Clamp(_window?.Opacity ?? 1, 0, 1));

    internal void Advance()
    {
        if (_cover == null || _image == null)
        {
            Stop();
            return;
        }
        float alpha = 1 - Math.Clamp((Environment.TickCount64 - _started) / (float)DurationMs, 0, 1);
        Alpha = alpha;
        _cover.SetAlpha(Level(alpha));
        if (alpha <= 0) Stop(); // the last frame was the window alone: hiding the cover changes nothing
    }

    public void Stop()
    {
        _timer.Stop();
        _cover?.Hide();
        _image?.Dispose();
        _image = null;
        _window = null;
    }

    public void Dispose()
    {
        Stop();
        _timer.Dispose();
        _cover?.DestroyHandle();
        _cover = null;
    }

    /// <summary>
    /// The layered window: a popup owned by the window (above it, hidden with it, never in the taskbar or Alt+Tab) whose image
    /// DWM blends with the window under it. WS_EX_TRANSPARENT with WS_EX_LAYERED: the mouse goes through it.
    /// </summary>
    private sealed class Cover : NativeWindow
    {
        public Cover(IntPtr owner)
        {
            Owner = owner;
            CreateHandle(new CreateParams
            {
                Caption = "",
                Style = unchecked((int)WS_POPUP),
                ExStyle = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
                Parent = owner, // a popup's owner
            });
        }

        public IntPtr Owner { get; }

        /// <summary>The bottom corners rounded with <paramref name="corner"/> pixels (0: square).</summary>
        internal int Corner { get; private set; }

        public bool Show(Bitmap image, Point at, byte alpha, int corner)
        {
            if (corner != Corner || corner > 0)
            {
                IntPtr region = IntPtr.Zero;
                if (corner > 0)
                {
                    region = CreateRoundRectRgn(0, 0, image.Width + 1, image.Height + 1, 2 * corner, 2 * corner);
                    IntPtr top = CreateRectRgn(0, 0, image.Width, Math.Max(0, image.Height - corner));
                    CombineRgn(region, region, top, RGN_OR); // only the bottom corners: the top is the title bar's edge
                    DeleteObject(top);
                }
                if (SetWindowRgn(Handle, region, false) == 0 && region != IntPtr.Zero) DeleteObject(region); // else the window owns it
                Corner = corner;
            }
            IntPtr screen = GetDC(IntPtr.Zero), memory = CreateCompatibleDC(screen), bitmap = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                bitmap = image.GetHbitmap();
                old = SelectObject(memory, bitmap);
                var position = new NativePoint { X = at.X, Y = at.Y };
                var size = new NativeSize { Width = image.Width, Height = image.Height };
                var origin = new NativePoint();
                var blend = Blend(alpha);
                if (!UpdateLayeredWindow(Handle, screen, ref position, ref size, memory, ref origin, 0, ref blend, ULW_ALPHA)) return false;
            }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(memory, old);
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                DeleteDC(memory);
                ReleaseDC(IntPtr.Zero, screen);
            }
            // Right above the window, not at the top: the window may be behind others (Windows' own theme switch comes while
            // its 设置 is in front) or under a window of its own, and the cover must not hide them.
            const int GWL_EXSTYLE = -20, WS_EX_TOPMOST = 0x0008;
            uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW;
            IntPtr above = GetWindow(Owner, GW_HWNDPREV);
            if (above == Handle) flags |= SWP_NOZORDER;
            else if (above == IntPtr.Zero || (GetWindowLong(above, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0) above = IntPtr.Zero; // HWND_TOP
            SetWindowPos(Handle, above, 0, 0, 0, 0, flags);
            return true;
        }

        public void SetAlpha(byte alpha)
        {
            var blend = Blend(alpha);
            UpdateLayeredWindow(Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref blend, ULW_ALPHA);
        }

        public void Hide()
        {
            if (Handle != IntPtr.Zero)
                SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_HIDEWINDOW);
        }

        // The image is opaque: its alpha channel is ignored (AlphaFormat 0), the constant alpha fades it.
        private static BlendFunction Blend(byte alpha) => new() { BlendOp = 0 /* AC_SRC_OVER */, SourceConstantAlpha = alpha };

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x0084, HTTRANSPARENT = -1, WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;
            if (m.Msg == WM_NCHITTEST) { m.Result = HTTRANSPARENT; return; }
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = MA_NOACTIVATE; return; }
            base.WndProc(ref m);
        }
    }

    internal const int WS_EX_LAYERED = 0x00080000, WS_EX_TRANSPARENT = 0x00000020, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_POPUP = 0x80000000;
    private const int ULW_ALPHA = 0x2, GW_HWNDPREV = 3;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010,
        SWP_SHOWWINDOW = 0x0040, SWP_HIDEWINDOW = 0x0080;
    private const uint RDW_INVALIDATE = 0x0001, RDW_ERASE = 0x0004, RDW_ALLCHILDREN = 0x0080, RDW_UPDATENOW = 0x0100, RDW_FRAME = 0x0400;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize { public int Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, out int value, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref NativePoint pptDst, ref NativeSize psize,
        IntPtr hdcSrc, ref NativePoint pptSrc, int crKey, ref BlendFunction pblend, int flags);

    /// <summary>Only the alpha: position, size and image stay.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, IntPtr psize,
        IntPtr hdcSrc, IntPtr pptSrc, int crKey, ref BlendFunction pblend, int flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr gdiObject);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    private const int RGN_OR = 2;

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr destination, IntPtr source1, IntPtr source2, int mode);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("user32.dll")]
    internal static extern int GetWindowRgnBox(IntPtr hwnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect { public int Left, Top, Right, Bottom; }

    /// <summary>Tests: the bottom-corner radius the cover was shown with.</summary>
    internal int CoverCorner => _cover?.Corner ?? 0;
}
