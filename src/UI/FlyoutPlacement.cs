using System.Reflection;
using System.Runtime.InteropServices;

namespace HomePodCast.UI;

/// <summary>Which side of its monitor the taskbar is on.</summary>
internal enum TaskbarEdge { Bottom, Top, Left, Right }

/// <summary>
/// Where the tray flyout goes. All rectangles are screen coordinates in physical pixels (the process is
/// per-monitor DPI aware): the flyout sits on the work-area side of the taskbar, centred on the tray icon
/// along the taskbar, and always inside the monitor's work area.
/// </summary>
internal static partial class FlyoutPlacement
{
    /// <summary>
    /// Top-left corner for a flyout of <paramref name="size"/> next to <paramref name="anchor"/> (the tray icon,
    /// or a 1×1 rectangle at the cursor): <paramref name="gap"/> away from the taskbar (or from an icon that sits
    /// above it, in the overflow area), and at least <paramref name="margin"/> inside the work area when it fits.
    /// A flyout larger than the work area keeps its top-left corner on screen.
    /// </summary>
    public static Point Place(Rectangle anchor, Rectangle workArea, TaskbarEdge edge, Size size, int gap, int margin)
    {
        int cx = anchor.Left + anchor.Width / 2, cy = anchor.Top + anchor.Height / 2;
        var (x, y) = edge switch
        {
            TaskbarEdge.Top => (cx - size.Width / 2, Math.Max(anchor.Bottom, workArea.Top) + gap),
            TaskbarEdge.Left => (Math.Max(anchor.Right, workArea.Left) + gap, cy - size.Height / 2),
            TaskbarEdge.Right => (Math.Min(anchor.Left, workArea.Right) - gap - size.Width, cy - size.Height / 2),
            _ => (cx - size.Width / 2, Math.Min(anchor.Top, workArea.Bottom) - gap - size.Height),
        };
        return new Point(Fit(x, size.Width, workArea.Left, workArea.Right, margin),
                         Fit(y, size.Height, workArea.Top, workArea.Bottom, margin));
    }

    private static int Fit(int position, int length, int start, int end, int margin)
    {
        if (length + 2 * margin <= end - start) return Math.Clamp(position, start + margin, end - margin - length);
        if (length <= end - start) return Math.Clamp(position, start, end - length);
        return start;
    }

    /// <summary>
    /// The taskbar's side, from how the work area is inset in the monitor; with an auto-hidden taskbar (no inset)
    /// the monitor edge nearest the icon.
    /// </summary>
    public static TaskbarEdge EdgeOf(Rectangle monitor, Rectangle workArea, Rectangle anchor)
    {
        (TaskbarEdge Edge, int Inset)[] insets =
        [
            (TaskbarEdge.Bottom, monitor.Bottom - workArea.Bottom),
            (TaskbarEdge.Top, workArea.Top - monitor.Top),
            (TaskbarEdge.Left, workArea.Left - monitor.Left),
            (TaskbarEdge.Right, monitor.Right - workArea.Right),
        ];
        var widest = insets.MaxBy(i => i.Inset); // first wins a tie: bottom
        if (widest.Inset > 0) return widest.Edge;

        int cx = anchor.Left + anchor.Width / 2, cy = anchor.Top + anchor.Height / 2;
        (TaskbarEdge Edge, int Distance)[] distances =
        [
            (TaskbarEdge.Bottom, monitor.Bottom - cy),
            (TaskbarEdge.Top, cy - monitor.Top),
            (TaskbarEdge.Left, cx - monitor.Left),
            (TaskbarEdge.Right, monitor.Right - cx),
        ];
        return distances.MinBy(d => d.Distance).Edge;
    }

    // ---------------------------------------------------------------- what the shell says

    /// <summary>The taskbar's side: the shell's answer when the taskbar is on this monitor, else <see cref="EdgeOf"/>.</summary>
    public static TaskbarEdge EdgeFor(Rectangle monitor, Rectangle workArea, Rectangle anchor)
    {
        try
        {
            var data = new AppBarData { Size = Marshal.SizeOf<AppBarData>() };
            if (SHAppBarMessage(AbmGetTaskbarPos, ref data) != UIntPtr.Zero && data.Edge <= 3 &&
                monitor.IntersectsWith(Rectangle.FromLTRB(data.Bounds.Left, data.Bounds.Top, data.Bounds.Right, data.Bounds.Bottom)))
            {
                return data.Edge switch
                {
                    0 => TaskbarEdge.Left,
                    1 => TaskbarEdge.Top,
                    2 => TaskbarEdge.Right,
                    _ => TaskbarEdge.Bottom,
                };
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"taskbar position: {ex.Message}");
        }
        return EdgeOf(monitor, workArea, anchor);
    }

    /// <summary>
    /// The tray icon's rectangle on screen, or null when the shell can't tell (e.g. Windows hides it). WinForms
    /// doesn't expose the icon's window and id that Shell_NotifyIconGetRect needs, so they are read by reflection.
    /// </summary>
    public static Rectangle? IconRect(NotifyIcon icon)
    {
        try
        {
            const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;
            if (typeof(NotifyIcon).GetField("_window", Instance)?.GetValue(icon) is not NativeWindow { Handle: var window } ||
                window == IntPtr.Zero ||
                typeof(NotifyIcon).GetField("_id", Instance)?.GetValue(icon) is not uint id)
            {
                return null;
            }
            var identifier = new NotifyIconIdentifier { Size = Marshal.SizeOf<NotifyIconIdentifier>(), Window = window, Id = id };
            if (Shell_NotifyIconGetRect(ref identifier, out var r) != 0) return null;
            var rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            return rect.Width > 0 && rect.Height > 0 ? rect : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"tray icon position: {ex.Message}");
            return null;
        }
    }

    private const uint AbmGetTaskbarPos = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public int Size;
        public IntPtr Window;
        public uint Id;
        public Guid Item;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public int Size;
        public IntPtr Window;
        public uint CallbackMessage;
        public uint Edge;
        public Rect Bounds;
        public IntPtr Param;
    }

    [LibraryImport("shell32.dll")]
    private static partial int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out Rect location);

    [LibraryImport("shell32.dll")]
    private static partial UIntPtr SHAppBarMessage(uint message, ref AppBarData data);
}
