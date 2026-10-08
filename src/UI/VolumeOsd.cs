using System.Runtime.InteropServices;
using HomePodCast.UI.Controls;

namespace HomePodCast.UI;

/// <summary>
/// The small "HomePod · volume bar · value" panel shown while the volume keys drive the HomePod
/// (<see cref="VolumeKeyMode.WhileStreaming"/>), at the bottom centre of the screen the user is looking at, like the
/// Windows 11 volume indicator. It never takes the focus (WS_EX_NOACTIVATE, shown without activation, click-through),
/// stays out of Alt+Tab and the taskbar (WS_EX_TOOLWINDOW), and is skipped while an exclusive full-screen Direct3D
/// program or presentation mode is in front (<see cref="Allowed"/>). Each update keeps it up; 1.5 s after the last
/// one it fades out and closes itself. Themed like the tray flyout; sized for its monitor's DPI.
/// </summary>
internal sealed partial class VolumeOsd : Form
{
    private const int WidthDp = 248, HeightDp = 54, BottomGapDp = 40;
    private const int FadeStepMs = 15, FadeMs = 240;
    private const int WsExTopmost = 0x8, WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExLayered = 0x80000, WsExNoActivate = 0x08000000;
    private const int WmMouseActivate = 0x21, MaNoActivate = 3;

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = FadeStepMs };
    private double? _percent;
    private bool _muted;
    private int _cap = 100;
    private long _holdUntil;
    private byte _alpha = 255;

    public VolumeOsd()
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        Text = "HomePod";
        DoubleBuffered = true;
        BackColor = Theme.P.Popup;
        _timer.Tick += (_, _) => Fade();
        Theme.Changed += OnThemeChanged;
    }

    /// <summary>How long it stays fully visible after the last update.</summary>
    public int HoldMs { get; set; } = 1500;

    // ---------------------------------------------------------------- when

    /// <summary>SHQueryUserNotificationState: QUNS_RUNNING_D3D_FULL_SCREEN and QUNS_PRESENTATION_MODE.</summary>
    private const int QunsRunningD3dFullScreen = 3, QunsPresentationMode = 4;

    /// <summary>No OSD over an exclusive full-screen Direct3D program (a game) or in presentation mode.</summary>
    public static bool AllowedFor(int notificationState) =>
        notificationState is not (QunsRunningD3dFullScreen or QunsPresentationMode);

    public static bool Allowed()
    {
        try
        {
            return SHQueryUserNotificationState(out int state) < 0 || AllowedFor(state);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return true;
        }
    }

    // ---------------------------------------------------------------- showing

    /// <summary>Show (or refresh) the panel: volume in percent (null = unknown), muted, and the cap for its mark.</summary>
    public void Present(double? percent, bool muted, int cap)
    {
        _percent = percent;
        _muted = muted;
        _cap = cap;
        Text = muted ? "HomePod" : $"HomePod {percent:0}";
        _holdUntil = Environment.TickCount64 + HoldMs;
        SetAlpha(255);
        if (!Visible)
        {
            Place();
            Show(); // ShowWithoutActivation: SW_SHOWNOACTIVATE
        }
        // Back on top of other topmost windows, without activating or moving.
        SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
        Invalidate();
        _timer.Start();
    }

    /// <summary>Bottom centre of the monitor with the foreground window (a game's), else the primary one.</summary>
    private void Place()
    {
        var foreground = GetForegroundWindow();
        var screen = foreground != IntPtr.Zero ? Screen.FromHandle(foreground) : Screen.PrimaryScreen ?? Screen.AllScreens[0];
        var work = screen.WorkingArea;
        if (!IsHandleCreated)
        {
            Location = new Point(work.Left + work.Width / 2, work.Top + work.Height / 2);
            _ = Handle; // created on that monitor, so DeviceDpi is its DPI
        }
        int w = Dp(WidthDp), h = Dp(HeightDp);
        Bounds = new Rectangle(work.Left + (work.Width - w) / 2, work.Bottom - Dp(BottomGapDp) - h, w, h);
    }

    private void Fade()
    {
        long now = Environment.TickCount64;
        if (now < _holdUntil) return;
        int step = 255 * FadeStepMs / FadeMs;
        if (_alpha <= step)
        {
            _timer.Stop();
            Close(); // TrayApp makes a new one next time (on whichever monitor is in front then)
            return;
        }
        SetAlpha((byte)(_alpha - step));
    }

    private void SetAlpha(byte alpha)
    {
        if (_alpha == alpha && IsHandleCreated) return;
        _alpha = alpha;
        if (IsHandleCreated) SetLayeredWindowAttributes(Handle, 0, alpha, LwaAlpha);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExNoActivate | WsExToolWindow | WsExTopmost | WsExTransparent | WsExLayered;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetLayeredWindowAttributes(Handle, 0, _alpha, LwaAlpha); // a layered window stays invisible until this
        Theme.StyleFlyout(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmMouseActivate)
        {
            m.Result = MaNoActivate;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Size = new Size(Dp(WidthDp), Dp(HeightDp));
        Invalidate();
    }

    private void OnThemeChanged()
    {
        if (IsDisposed) return;
        BackColor = Theme.P.Popup;
        if (IsHandleCreated) Theme.StyleFlyout(Handle);
        Invalidate();
    }

    // ---------------------------------------------------------------- painting

    private int Dp(float value) => Theme.Dp(value, DeviceDpi);

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var p = Theme.P;
        e.Graphics.Clear(p.Popup);
        if (!Theme.RoundedWindows) // Windows 10: no DWM border on a borderless window
        {
            using var pen = new Pen(p.PopupBorder, Math.Max(1, DeviceDpi / 96));
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = Theme.P;
        int dpi = DeviceDpi;
        int padX = Dp(16), icon = Dp(24), gap = Dp(12);

        // speaker or muted speaker
        var iconRect = new Rectangle(padX, (Height - icon) / 2, icon, icon);
        Shapes.Glyph(g, _muted ? Glyph.Mute : Glyph.Volume, Theme.IconFont(20, dpi), iconRect, _muted ? p.Critical : p.Text);

        int x = iconRect.Right + gap, right = Width - padX;
        var nameFont = Theme.Font(TextStyle.Caption, dpi);
        var valueFont = Theme.Font(TextStyle.BodyStrong, dpi);
        string value = _percent is { } v ? $"{Math.Round(v):0}" : "--";
        int lineH = TextRenderer.MeasureText("Ag", valueFont, Size.Empty, TextFlags.Measure).Height;
        int barT = Dp(4), barGap = Dp(7);
        int top = (Height - (lineH + barGap + barT)) / 2;

        int valueW = TextRenderer.MeasureText("100", valueFont, Size.Empty, TextFlags.Measure).Width;
        TextRenderer.DrawText(g, "HomePod", nameFont, new Rectangle(x, top, right - x - valueW - gap, lineH), p.TextSecondary, TextFlags.Line);
        TextRenderer.DrawText(g, value, valueFont, new Rectangle(right - valueW, top, valueW, lineH), _muted ? p.TextDisabled : p.Text,
            TextFlags.Line | TextFormatFlags.Right);

        // the bar: the whole width is 0…100 %, a tick marks the cap
        var bar = new Rectangle(x, top + lineH + barGap, right - x, barT);
        Shapes.FillRound(g, p.MeterTrack, bar, barT / 2f);
        double fraction = Math.Clamp((_percent ?? 0) / 100.0, 0, 1);
        int fill = (int)Math.Round(bar.Width * fraction);
        if (fill > 0) Shapes.FillRound(g, _muted ? p.TextDisabled : p.Accent, new Rectangle(bar.X, bar.Y, Math.Max(fill, barT), barT), barT / 2f);
        if (_cap < 100)
        {
            int capX = bar.X + (int)Math.Round(bar.Width * _cap / 100.0);
            using var tick = new SolidBrush(p.TextSecondary);
            g.FillRectangle(tick, capX - Math.Max(1, dpi / 96) / 2, bar.Y - Dp(3), Math.Max(1, dpi / 96), barT + Dp(6));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= OnThemeChanged;
            _timer.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---------------------------------------------------------------- Win32

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x1, SwpNoMove = 0x2, SwpNoActivate = 0x10, SwpShowWindow = 0x40;
    private const uint LwaAlpha = 0x2;

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int state);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
}
