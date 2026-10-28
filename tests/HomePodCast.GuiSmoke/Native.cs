using System.Runtime.InteropServices;
using System.Text;

namespace HomePodCast.GuiSmoke;

/// <summary>Win32 calls the smoke test needs beyond UI Automation: windows, scrolling, capture, DPI, display, MSAA.</summary>
internal static partial class Native
{
    public const int WM_CLOSE = 0x0010, WM_VSCROLL = 0x0115;
    public const int SB_LINEDOWN = 1, SB_PAGEDOWN = 3, SB_TOP = 6;
    public const int SB_VERT = 1;
    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    public const long WS_VSCROLL = 0x00200000, WS_DISABLED = 0x08000000;
    public const long WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8;
    public const uint SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    public const uint OBJID_CLIENT = 0xFFFFFFFC;
    public const int STATE_SYSTEM_CHECKED = 0x10, STATE_SYSTEM_SELECTED = 0x2, STATE_SYSTEM_UNAVAILABLE = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SCROLLINFO
    {
        public int cbSize, fMask, nMin, nMax, nPage, nPos, nTrackPos;
    }

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, int flags, int timeoutMs, out IntPtr result);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetScrollInfo(IntPtr hwnd, int bar, ref SCROLLINFO info);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(Point pt, uint flags);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
    [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint id, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object? acc);

    public static readonly IntPtr DpiAwarenessPerMonitorV2 = new(-4);

    public static string WindowText(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutText(IntPtr hwnd, int msg, IntPtr wParam, StringBuilder text, int flags, int timeoutMs, out IntPtr result);

    /// <summary>A control's own text in another process (WM_GETTEXT; GetWindowText only reads captions across processes).</summary>
    public static string ControlText(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        if (SendMessageTimeout(hwnd, 0x000E /* WM_GETTEXTLENGTH */, IntPtr.Zero, IntPtr.Zero, 0x2, 2000, out var length) == IntPtr.Zero) return "";
        var sb = new StringBuilder((int)Math.Clamp((long)length + 1, 1, 65536));
        return SendMessageTimeoutText(hwnd, 0x000D /* WM_GETTEXT */, new IntPtr(sb.Capacity), sb, 0x2, 2000, out _) == IntPtr.Zero ? "" : sb.ToString();
    }

    public static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    // NativeWindow registers a framework-prefixed subclass of common controls (including ToolTip).
    internal static bool IsToolTipClass(string name) =>
        name.Equals("tooltips_class32", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("WindowsForms10.tooltips_class32.", StringComparison.OrdinalIgnoreCase);

    public static long Style(IntPtr hwnd) => (long)GetWindowLongPtr(hwnd, GWL_STYLE);
    public static long ExStyle(IntPtr hwnd) => (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);

    /// <summary>Top-level windows of a process, optionally including hidden windows for failure diagnostics.</summary>
    public static List<IntPtr> TopLevelWindows(int pid, bool visibleOnly = true)
    {
        var list = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint owner);
            if (owner == pid && (!visibleOnly || IsWindowVisible(hwnd))) list.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>The window as it is seen: DWM's frame bounds (no invisible resize borders), else the window rect.</summary>
    public static Rectangle VisibleBounds(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var frame, Marshal.SizeOf<RECT>()) == 0)
        {
            var r = frame.ToRectangle();
            if (r.Width > 0 && r.Height > 0) return r;
        }
        GetWindowRect(hwnd, out var w);
        return w.ToRectangle();
    }

    public static Rectangle WindowRect(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var w);
        return w.ToRectangle();
    }

    public static SCROLLINFO? VerticalScroll(IntPtr hwnd)
    {
        var info = new SCROLLINFO { cbSize = Marshal.SizeOf<SCROLLINFO>(), fMask = 0x17 /* SIF_ALL */ };
        return GetScrollInfo(hwnd, SB_VERT, ref info) ? info : null;
    }

    /// <summary>The effective DPI of the primary monitor now (follows a scale change at once).</summary>
    public static uint PrimaryMonitorDpi()
    {
        var monitor = MonitorFromPoint(new Point(0, 0), 1 /* MONITOR_DEFAULTTOPRIMARY */);
        return GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out uint x, out _) == 0 ? x : 0;
    }

    // ---------------------------------------------------------------- MSAA (values UI Automation's managed API can't read)

    private static Guid IidAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");

    public static Accessibility.IAccessible? Msaa(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        try
        {
            return AccessibleObjectFromWindow(hwnd, OBJID_CLIENT, ref IidAccessible, out var acc) == 0 ? acc as Accessibility.IAccessible : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>accDescription of a control's window (MetricTile puts its value there).</summary>
    public static string? MsaaDescription(IntPtr hwnd)
    {
        try { return Msaa(hwnd)?.get_accDescription(0); } catch (Exception) { return null; }
    }

    public static string? MsaaValue(IntPtr hwnd)
    {
        try { return Msaa(hwnd)?.get_accValue(0); } catch (Exception) { return null; }
    }

    public static int? MsaaState(IntPtr hwnd)
    {
        try { return Msaa(hwnd)?.get_accState(0) is int state ? state : null; } catch (Exception) { return null; }
    }

    public static bool MsaaDoDefaultAction(IntPtr hwnd)
    {
        try
        {
            var acc = Msaa(hwnd);
            if (acc == null) return false;
            acc.accDoDefaultAction(0);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- capture

    /// <summary>
    /// The window alone, as DWM composes it (PrintWindow with PW_RENDERFULLCONTENT): other windows on top don't show,
    /// cropped to the visible frame. Falls back to a copy of its screen area when that gives nothing.
    /// </summary>
    public static Bitmap Capture(IntPtr hwnd)
    {
        var window = WindowRect(hwnd);
        var visible = VisibleBounds(hwnd);
        if (window.Width <= 0 || window.Height <= 0) throw new InvalidOperationException("window has no size");
        using var full = new Bitmap(window.Width, window.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        bool printed;
        using (var g = Graphics.FromImage(full))
        {
            var hdc = g.GetHdc();
            try { printed = PrintWindow(hwnd, hdc, 2 /* PW_RENDERFULLCONTENT */); }
            finally { g.ReleaseHdc(hdc); }
        }
        var crop = Rectangle.Intersect(new Rectangle(visible.X - window.X, visible.Y - window.Y, visible.Width, visible.Height),
            new Rectangle(Point.Empty, full.Size));
        if (crop.Width <= 0 || crop.Height <= 0) crop = new Rectangle(Point.Empty, full.Size);
        var shot = full.Clone(crop, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        if (printed && !UniformClient(shot)) return shot;
        shot.Dispose();
        var screen = new Bitmap(visible.Width, visible.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(screen)) g.CopyFromScreen(visible.Location, Point.Empty, visible.Size);
        return screen;
    }

    /// <summary>Sample the client interior: a painted title bar does not prove the window content was captured.</summary>
    internal static bool UniformClient(Bitmap bitmap)
    {
        // 2026-10-26: PrintWindow sometimes returned a normal confirmation title bar above a black client.
        int left = bitmap.Width / 10, top = bitmap.Height / 5;
        int right = Math.Max(left + 1, bitmap.Width * 9 / 10), bottom = Math.Max(top + 1, bitmap.Height * 4 / 5);
        var first = bitmap.GetPixel(left, top);
        for (int y = top; y < bottom; y += Math.Max(1, bitmap.Height / 40))
        for (int x = left; x < right; x += Math.Max(1, bitmap.Width / 40))
            if (bitmap.GetPixel(x, y) != first) return false;
        return true;
    }
}
