using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using HomePodCast.UI;
using HomePodCast.UI.Controls;
using HomePodCast.UI.Pages;
using Xunit.Abstractions;

namespace HomePodCast.Tests;

/// <summary>
/// The fade between the themes, in the real main window (off-screen, Opacity 0: its cover is as transparent). 2026-10-27, the
/// maintainer: in dark mode, after a click on 「浅色」, the bottom of the rail jumped at the end of the fade. The fade was never
/// tested: an invisible test window never animated. <see cref="ThemeTransition.ForceAnimation"/> makes it fade here.
/// </summary>
[Collection("Layout work")]
public class ThemeTransitionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(Theme.Dark)]
    [InlineData(Theme.Light)]
    public void The_fade_starts_on_the_finished_window_and_ends_on_it_unchanged(string start) => WithTheme(start, () =>
    {
        var config = TestApp.Config(); config.Theme = start;
        using var window = new MainWindow(TestApp.Create(config));
        ShowMain(window);
        Go(window, AppPage.Settings);
        var transition = TestApp.Field<ThemeTransition>(window, "_transition");
        using var before = ThemeTransition.Snapshot(window)!;
        Bitmap? cover = null, atStart = null;
        List<string>? unpainted = null;
        float alphaAtStart = -1;
        transition.FadeStarted += () =>
        {
            unpainted = Unpainted(window);
            alphaAtStart = transition.Alpha;
            cover = new Bitmap(transition.CoverImage!);
            atStart = ThemeTransition.Snapshot(window);
            AssertCover(window, transition);
        };
        using (var work = new Work())
        {
            window.ToggleTheme();
            Assert.True(transition.Running);
            Pump(() => !transition.Running, 5000);
            work.AssertOnce("faded theme change", output); // showing and hiding the cover lays nothing out
            Assert.Equal(1, work.Pages);
        }
        Assert.Equal(1, transition.Snapshots); // one snapshot: the window as it was
        Assert.Equal(1f, alphaAtStart); // the clock starts with the cover still opaque
        // Every window in it painted in the new theme before the fade began, under the cover, not one by one at its end.
        Assert.True(unpainted!.Count == 0, "left to paint when the fade started: " + string.Join(", ", unpainted));
        AssertSame(before, cover!, null, window, "the cover shows the window as it was");
        Assert.NotNull(FirstDifference(before, atStart!, null)); // and the theme did change under it
        using var after = ThemeTransition.Snapshot(window)!;
        AssertSame(atStart!, after, null, window, "the window under the cover changed by the end of the fade");
        Assert.Equal(0f, transition.Alpha); // the last frame was the window alone
        Assert.False(IsWindowVisible(transition.CoverHandle));
        cover!.Dispose(); atStart!.Dispose();
        window.Hide();
    });

    [Fact]
    public void A_click_on_the_theme_button_is_captured_after_the_mouse_up_and_leaves_it_the_mouse() => WithTheme(Theme.Dark, () =>
    {
        var config = TestApp.Config(); config.Theme = Theme.Dark;
        using var window = new MainWindow(TestApp.Create(config));
        ShowMain(window);
        var transition = TestApp.Field<ThemeTransition>(window, "_transition");
        var button = TestApp.Field<NavItem>(TestApp.Field<NavRail>(window, "_rail"), "_themeButton");
        var mouse = new MouseEventArgs(MouseButtons.Left, 1, 5, 5, 0);
        // The button pressed and released once without a click: how it looks hovered after a click.
        Call(button, "OnMouseEnter", EventArgs.Empty);
        Call(button, "OnMouseDown", mouse);
        Call(button, "OnMouseUp", mouse);
        using var released = ThemeTransition.Snapshot(window)!;
        Bitmap? cover = null;
        bool hoverAtStart = false;
        transition.FadeStarted += () =>
        {
            cover = new Bitmap(transition.CoverImage!);
            hoverAtStart = Hover(button);
            var centre = button.RectangleToScreen(button.ClientRectangle);
            centre = new Rectangle(centre.X + centre.Width / 2, centre.Y + centre.Height / 2, 0, 0);
            const int WM_NCHITTEST = 0x0084;
            Assert.Equal(-1, (int)SendMessage(transition.CoverHandle, WM_NCHITTEST, 0, (centre.X & 0xFFFF) | (centre.Y << 16))); // HTTRANSPARENT
        };
        // The click, as WinForms raises it: OnClick before OnMouseUp.
        Call(button, "OnMouseDown", mouse);
        Call(button, "OnClick", EventArgs.Empty);
        Assert.Equal(0, transition.Snapshots); // not while the button is still pressed
        Call(button, "OnMouseUp", mouse);
        Pump(() => transition.Snapshots == 1 && !transition.Running, 5000);
        Assert.Equal(Theme.Light, config.Theme);
        var area = window.RectangleToClient(button.RectangleToScreen(button.ClientRectangle));
        AssertSame(released, cover!, area, window, "the theme button as the fade began"); // hovered, not pressed
        Assert.True(hoverAtStart);
        Assert.True(Hover(button)); // the cover never took the mouse
        cover!.Dispose();
        window.Hide();
    });

    [Fact]
    public void Settings_appearance_fades_like_the_rail_button() => WithTheme(Theme.Light, () =>
    {
        var config = TestApp.Config(); config.Theme = Theme.Light;
        using var window = new MainWindow(TestApp.Create(config));
        ShowMain(window);
        Go(window, AppPage.Settings);
        var transition = TestApp.Field<ThemeTransition>(window, "_transition");
        var combo = TestApp.Field<FluentComboBox>(TestApp.Field<SettingsPage>(window, "_settings"), "_theme");
        using (var work = new Work())
        {
            // 「深色」 picked in the list, as its menu item does.
            typeof(FluentComboBox).GetMethod("Select", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(int), typeof(bool)])!
                .Invoke(combo, [2, true]);
            Pump(() => transition.Snapshots == 1 && !transition.Running, 5000);
            work.AssertOnce("外观 → 深色", output);
            Assert.Equal(1, work.Pages);
        }
        Assert.Equal(Theme.Dark, config.Theme);
        Assert.Equal(Theme.Dark, Theme.Mode);
        Assert.True(Theme.IsDark);
        Assert.Equal(2, combo.SelectedIndex);
        window.Hide();
    });

    [Fact]
    public void Windows_switching_its_theme_fades_the_window_once() => WithTheme(Theme.Light, () =>
    {
        using var window = new MainWindow(TestApp.Create(TestApp.Config()));
        ShowMain(window);
        var transition = TestApp.Field<ThemeTransition>(window, "_transition");
        var system = typeof(MainWindow).GetMethod("OnSystemTheme", BindingFlags.Instance | BindingFlags.NonPublic)!;
        // As 跟随 Windows sees Windows switch to dark: the mode now gives another palette than the one shown.
        typeof(Theme).GetField("_mode", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, Theme.Dark);
        system.Invoke(window, null);
        Pump(() => !transition.Running, 5000);
        Assert.Equal(1, transition.Snapshots);
        Assert.True(Theme.IsDark);
        system.Invoke(window, null); // another setting changed: nothing to fade
        Assert.Equal(1, transition.Snapshots);
        Assert.False(transition.Running);
        window.Hide();
    });

    [Fact]
    public void A_second_change_during_the_fade_goes_on_from_the_frame_on_screen() => WithTheme(Theme.Dark, () =>
    {
        var config = TestApp.Config(); config.Theme = Theme.Dark;
        using var window = new MainWindow(TestApp.Create(config));
        ShowMain(window);
        Go(window, AppPage.About);
        var transition = TestApp.Field<ThemeTransition>(window, "_transition");
        window.ToggleTheme();
        Pump(() => transition.Alpha < 0.8f || !transition.Running, 5000);
        Assert.True(transition.Running);
        // On screen now: the dark cover at this alpha over the light window.
        float alpha = transition.Alpha;
        using var shown = Blend(transition.CoverImage!, ThemeTransition.Snapshot(window)!, alpha);
        Bitmap? cover = null;
        transition.FadeStarted += () => cover = new Bitmap(transition.CoverImage!);
        window.ToggleTheme(); // a double click
        Assert.Equal(2, transition.Snapshots);
        Assert.Equal(1f, transition.Alpha);
        AssertSame(shown, cover!, null, window, $"the second fade's first frame (first fade at {alpha:0.00})", tolerance: 2);
        Pump(() => !transition.Running, 5000);
        Assert.Equal(Theme.Dark, config.Theme);
        Assert.True(Theme.IsDark);
        cover!.Dispose();
        window.Hide();
    });

    // ---------------------------------------------------------------- helpers

    /// <summary>The cover: a window of its own, owned by the window, right above it, never taking the mouse or activation.</summary>
    private static void AssertCover(MainWindow window, ThemeTransition transition)
    {
        var cover = transition.CoverHandle;
        Assert.NotEqual(IntPtr.Zero, cover);
        Assert.True(IsWindowVisible(cover));
        Assert.Null(Control.FromHandle(cover)); // not a control of the window: showing it lays nothing out
        Assert.Equal(window.Handle, GetWindow(cover, 4)); // GW_OWNER
        Assert.Equal(window.Handle, GetWindow(cover, 2)); // GW_HWNDNEXT: right above it
        int ex = GetWindowLong(cover, -20);
        int wanted = ThemeTransition.WS_EX_LAYERED | ThemeTransition.WS_EX_TRANSPARENT | ThemeTransition.WS_EX_NOACTIVATE | ThemeTransition.WS_EX_TOOLWINDOW;
        Assert.Equal(wanted, ex & wanted);
        GetWindowRect(cover, out var r);
        Assert.Equal(window.RectangleToScreen(window.ClientRectangle), Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom));
    }

    /// <summary>The visible windows in <paramref name="window"/> with something left to paint.</summary>
    private static List<string> Unpainted(Form window)
    {
        var handles = new List<IntPtr> { window.Handle };
        EnumChildWindows(window.Handle, (h, _) => { handles.Add(h); return true; }, IntPtr.Zero);
        return handles.Where(h => IsWindowVisible(h) && GetUpdateRect(h, IntPtr.Zero, false))
            .Select(h => Control.FromHandle(h) is { } c ? $"{c.GetType().Name} 「{c.AccessibleName ?? c.Text}」" : $"0x{h:X}").ToList();
    }

    private static Bitmap Blend(Bitmap top, Bitmap bottom, float alpha)
    {
        var result = new Bitmap(bottom.Width, bottom.Height, PixelFormat.Format32bppArgb);
        var a = Pixels(top); var b = Pixels(bottom); var r = new int[b.Length];
        for (int i = 0; i < r.Length; i++)
        {
            int Channel(int shift) => (int)Math.Round(((a[i] >> shift) & 0xFF) * alpha + ((b[i] >> shift) & 0xFF) * (1 - alpha));
            r[i] = unchecked((int)0xFF000000) | Channel(16) << 16 | Channel(8) << 8 | Channel(0);
        }
        var data = result.LockBits(new Rectangle(Point.Empty, result.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(r, 0, data.Scan0, r.Length);
        result.UnlockBits(data);
        bottom.Dispose();
        return result;
    }

    private static int[] Pixels(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new int[bitmap.Width * bitmap.Height];
            for (int y = 0; y < bitmap.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * bitmap.Width, bitmap.Width);
            return pixels;
        }
        finally { bitmap.UnlockBits(data); }
    }

    /// <summary>The first pixel (in <paramref name="area"/>) where the two differ by more than <paramref name="tolerance"/> per channel.</summary>
    private static (Point At, Color Expected, Color Actual)? FirstDifference(Bitmap expected, Bitmap actual, Rectangle? area, int tolerance = 0)
    {
        Assert.Equal(expected.Size, actual.Size);
        var e = Pixels(expected); var a = Pixels(actual);
        var r = Rectangle.Intersect(area ?? new Rectangle(Point.Empty, expected.Size), new Rectangle(Point.Empty, expected.Size));
        for (int y = r.Top; y < r.Bottom; y++)
        for (int x = r.Left; x < r.Right; x++)
        {
            int i = y * expected.Width + x;
            Color ce = Color.FromArgb(e[i]), ca = Color.FromArgb(a[i]);
            if (Math.Abs(ce.R - ca.R) > tolerance || Math.Abs(ce.G - ca.G) > tolerance || Math.Abs(ce.B - ca.B) > tolerance || ce.A != ca.A)
                return (new Point(x, y), ce, ca);
        }
        return null;
    }

    private static void AssertSame(Bitmap expected, Bitmap actual, Rectangle? area, Control window, string what, int tolerance = 0)
    {
        if (FirstDifference(expected, actual, area, tolerance) is not { } d) return;
        var at = window.PointToScreen(d.At);
        var control = window;
        while (control.Controls.Cast<Control>().LastOrDefault(c => c.Visible && c.RectangleToScreen(c.ClientRectangle).Contains(at)) is { } child)
            control = child;
        Assert.Fail($"{what}: differs at {d.At} in {control.GetType().Name} 「{control.AccessibleName ?? control.Text}」: " +
                    $"expected {d.Expected}, was {d.Actual}");
    }

    private static bool Hover(FluentControl control) =>
        (bool)typeof(FluentControl).GetProperty("IsHover", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(control)!;

    private static void Call(Control control, string method, EventArgs e) =>
        typeof(Control).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic, [e.GetType()])!.Invoke(control, [e]);

    /// <summary>On an STA thread, with the theme set to <paramref name="mode"/> and the fade forced, both set back afterwards.</summary>
    private static void WithTheme(string mode, Action body) => Sta.Run(() =>
    {
        string previous = Theme.Mode;
        try
        {
            Theme.Mode = mode;
            ThemeTransition.ForceAnimation = true;
            body();
        }
        finally
        {
            ThemeTransition.ForceAnimation = false;
            Theme.Mode = previous;
        }
    });

    /// <summary>The main window shown off-screen and transparent (LayoutWorkTests.ShowMain), its pages left to their first visit.</summary>
    private static void ShowMain(MainWindow window)
    {
        window.PrepareAhead = false;
        window.StartPosition = FormStartPosition.Manual;
        window.Location = new Point(-20000, -20000);
        window.Opacity = 0;
        window.ShowInTaskbar = false;
        window.FormBorderStyle = FormBorderStyle.None;
        window.Show();
        Application.DoEvents();
    }

    private static void Go(MainWindow window, AppPage page)
    {
        window.ShowPage(page);
        Application.DoEvents();
    }

    private static void Pump(Func<bool> done, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!done())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException($"still waiting after {timeoutMs} ms");
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    /// <summary>Layouts per container and texts measured on this thread while it is in use (LayoutWorkTests.Work).</summary>
    private sealed class Work : IDisposable
    {
        private readonly Dictionary<Control, int> _arranged = new();
        private readonly List<string> _measured = [];

        public Work()
        {
            LayoutPanel.Arranged = c => _arranged[c] = _arranged.GetValueOrDefault(c) + 1;
            TextMeasure.Measured = (text, font, size, flags) =>
                _measured.Add($"「{text}」 {font.Name} {font.Style} {font.Size}px (font #{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(font)}) {size} {flags}");
        }

        public int Pages => _arranged.Keys.Count(c => c is ScrollPage);

        public void AssertOnce(string what, ITestOutputHelper output)
        {
            output.WriteLine($"{what}: {_arranged.Count} containers laid out, {_measured.Count} texts measured");
            var twice = _arranged.Where(kv => kv.Value > 1).Select(kv => $"{kv.Key.GetType().Name} ×{kv.Value}").ToList();
            Assert.True(twice.Count == 0, $"{what}: laid out more than once: {string.Join(", ", twice.Take(10))}");
            var again = _measured.GroupBy(m => m).Where(g => g.Count() > 1).Select(g => $"{g.Key} ×{g.Count()}").ToList();
            Assert.True(again.Count == 0, $"{what}: measured more than once: {string.Join("; ", again.Take(10))}");
        }

        public void Dispose()
        {
            LayoutPanel.Arranged = null;
            TextMeasure.Measured = null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool GetUpdateRect(IntPtr hwnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, nint wParam, nint lParam);
}
