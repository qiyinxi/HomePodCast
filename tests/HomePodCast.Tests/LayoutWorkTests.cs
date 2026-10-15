using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using HomePodCast.Audio;
using HomePodCast.UI;
using HomePodCast.UI.Controls;
using HomePodCast.UI.Pages;
using HomePodCast.Update;
using Xunit.Abstractions;

namespace HomePodCast.Tests;

// One DPI test makes WinForms follow DPI messages on its own thread (a process-wide switch, set back afterwards): these
// tests never run next to another test.
[CollectionDefinition("Layout work", DisableParallelization = true)]
public sealed class LayoutWorkCollection { }

/// <summary>
/// How much layout work the real pages do (built off-screen with a <see cref="TestApp"/>, in an invisible window): layouts
/// per container, texts measured, windows created. Counts, not times: 2026-10-15 on 1.1.0-beta.5 a toggle in 设置 measured
/// 2188 texts (161 different ones), switching to 混音器 created 70 windows each time, and moving the window to a monitor
/// with another scale laid 设置 out 282 times (8 s 「未响应」).
/// </summary>
[Collection("Layout work")]
public class LayoutWorkTests
{
    private readonly ITestOutputHelper output;

    /// <summary>Each test starts with the text cache empty, so that what it counts never meets the cache starting over.</summary>
    public LayoutWorkTests(ITestOutputHelper output)
    {
        this.output = output;
        TextMeasure.Clear();
    }

    [Fact]
    public void Toggling_night_disconnect_lays_each_container_out_once_and_measures_each_text_once() => Sta.Run(() =>
    {
        var config = TestApp.Config();
        using var host = new Host(new SettingsPage(TestApp.Create(config)), 784);
        var page = (SettingsPage)host.Page;
        var night = TestApp.Field<ToggleSwitch>(page, "_night");
        var nightRow = (SettingRow)night.Parent!;
        var windowRow = TestApp.Field<SettingRow>(page, "_nightWindowRow");
        var idleRow = TestApp.Field<SettingRow>(page, "_nightIdleRow");
        Assert.True(windowRow.Collapsed && idleRow.Collapsed);

        using (var work = new Work())
        {
            night.Flip(); // on: the two rows show for the first time
            work.AssertOnce("夜间自动断开 on", output);
        }
        Assert.True(config.NightDisconnect);
        Assert.False(windowRow.Collapsed || idleRow.Collapsed);
        int gap = Theme.Dp(4, page.DeviceDpi);
        Assert.Equal(nightRow.Bottom + gap, windowRow.Top);
        Assert.Equal(windowRow.Bottom + gap, idleRow.Top);
        Assert.Equal(idleRow.Bottom + Theme.Dp(20, page.DeviceDpi), idleRow.Parent!.Controls[idleRow.Parent.Controls.GetChildIndex(idleRow) + 1].Top);

        using (var work = new Work())
        {
            night.Flip(); // off
            work.AssertOnce("夜间自动断开 off", output);
            night.Flip(); // on again
            Assert.Empty(work.Measured); // every text was measured before
        }
        Assert.Equal(nightRow.Bottom + gap, windowRow.Top);
    });

    [Fact]
    public void An_update_found_after_the_page_was_built_shows_its_buttons_in_order() => Sta.Run(() =>
    {
        // The buttons were collapsed when 设置 was built (no update yet), and are shown last to first: WinForms put 查看说明
        // before 更新 when it sorted them after their windows (LayoutPanel.KeepChildOrder).
        var app = TestApp.Create(TestApp.Config());
        using var host = new Host(new AboutPage(app), 784);
        var next = new Release(ReleaseVersion.Parse("9.0.0-beta.1")!, "v9.0.0-beta.1", "https://example.invalid", true, null);
        typeof(TrayApp).GetField("_update", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, next);
        typeof(TrayApp).GetField("_updateStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, "发现新版本 9.0.0-beta.1");
        ((AboutPage)host.Page).ShowUpdate();
        Application.DoEvents();
        var buttons = TestApp.Field<UpdateStatusRow>(host.Page, "_updateRow").Buttons.Controls.Cast<Control>().Where(c => !LayoutPanel.IsCollapsed(c)).ToList();
        Assert.Equal(["更新", "查看说明", "跳过此版本"], buttons.Select(b => b.Text));
        Assert.True(buttons[0].Right < buttons[1].Left && buttons[1].Right < buttons[2].Left);
    });

    [Fact]
    public void A_speaker_info_read_lays_out_the_speakers_page_once_each_time_it_shows() => Sta.Run(() =>
    {
        using var host = new Host(new SpeakersPage(TestApp.Create(TestApp.Config())), 784);
        var info = TestApp.Field<SpeakerInfoPanel>(host.Page, "_speakerInfo");
        var refresh = TestApp.Field<FluentButton>(info, "_refresh");
        int changes = 0;
        var cache = TestApp.Field<Net.SpeakerInfoCache>(info, "_cache");
        cache.Changed += _ => Interlocked.Increment(ref changes); // on the thread pool; each one is shown once (posted ShowAll)
        using var work = new Work();
        refresh.PerformClick(); // shows what is known at once, then the read: reading, read
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (cache.Get(TestApp.DeviceId)?.State != Net.SpeakerInfoState.Ready && DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(20);
        }
        for (int i = 0; i < 10; i++)
        {
            Application.DoEvents();
            Thread.Sleep(20);
        }
        int layouts = work.Arranged.GetValueOrDefault(host.Page);
        output.WriteLine($"刷新: {changes} changes, {layouts} layouts of 设置");
        Assert.True(changes >= 1);
        Assert.Equal(1 + changes, layouts); // before: several for each (every changed text asked for one)
    });

    [Fact]
    public void Switching_pages_lays_out_the_shown_page_once_and_keeps_the_mixer_rows() => Sta.Run(() =>
    {
        var app = TestApp.Create(TestApp.Config());
        using var window = new MainWindow(app);
        var mixer = TestApp.Field<MixerPage>(window, "_mixer");
        List<(uint Pid, string Name)> playing = [(9001, "Game"), (9002, "Browser")];
        mixer.FindApps = _ => playing.Select(p => new AppAudio(p.Pid, false, p.Name, null) { ExeKey = p.Name.ToLowerInvariant() + ".exe" }).ToList();
        ShowHidden(window);
        AppPage[] all = [AppPage.Home, AppPage.Mixer, AppPage.Effects, AppPage.Settings];
        foreach (var page in all) Go(window, page); // every page once: its windows and texts
        Assert.Equal(2, MixerRows(mixer).Count);

        foreach (var (from, to) in new[] { (AppPage.Settings, AppPage.Home), (AppPage.Home, AppPage.Mixer), (AppPage.Mixer, AppPage.Effects),
                     (AppPage.Effects, AppPage.Settings), (AppPage.Settings, AppPage.Mixer), (AppPage.Mixer, AppPage.Home) })
        {
            Assert.Equal(from, window.CurrentPage);
            var rows = MixerRows(mixer);
            int handles = Handles(window);
            using (var work = new Work())
            {
                Go(window, to);
                work.AssertOnce($"{from} → {to}", output);
                Assert.Equal(1, work.Pages);
            }
            Assert.Equal(handles, Handles(window)); // no window created (or destroyed) for a page shown before
            Assert.Equal(rows, MixerRows(mixer));
        }

        // Another app plays meanwhile: the rows are built again, once, when the page is shown.
        playing.Add((9003, "Voice chat"));
        Go(window, AppPage.Mixer);
        Assert.Equal(["Game", "Browser", "Voice chat"], MixerRows(mixer).Select(r => r.Controls.OfType<TextBlock>().First().Text));
        window.Hide();
    });

    [Fact]
    public void Moving_the_window_to_another_monitor_s_scale_lays_it_out_once() => Sta.Run(() =>
    {
        // As the app runs (Program: per-monitor v2), on this thread only; the test process itself is not DPI aware.
        var helper = Type.GetType("System.Windows.Forms.ScaleHelper, System.Windows.Forms.Primitives", throwOnError: true)!;
        var perMonitor = helper.GetField("s_processPerMonitorAware", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? before = perMonitor.GetValue(null);
        perMonitor.SetValue(null, true);
        try
        {
            SetThreadDpiAwarenessContext(new IntPtr(-4)); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
            var app = TestApp.Create(TestApp.Config());
            using var window = new MainWindow(app);
            ShowHidden(window);
            foreach (var page in new[] { AppPage.Home, AppPage.Mixer, AppPage.Effects, AppPage.Settings }) Go(window, page);
            int start = window.DeviceDpi, other = start == 96 ? 144 : 96; // 150 % ↔ 100 %, the maintainer's two monitors
            foreach (int dpi in new[] { other, start })
            {
                using (var work = new Work())
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    ChangeDpi(window, dpi);
                    output.WriteLine($"→ {dpi} dpi: {watch.ElapsedMilliseconds} ms, {work.Arranged.Count} containers laid out");
                    work.AssertOnce($"→ {dpi} dpi", output);
                    Assert.Equal(1, work.Pages);
                }
                Assert.Equal(dpi, window.DeviceDpi);
                var settings = TestApp.Field<SettingsPage>(window, "_settings");
                var night = TestApp.Field<ToggleSwitch>(settings, "_night");
                Assert.Equal(dpi, night.DeviceDpi);
                Assert.Equal(night.GetPreferredSize(Size.Empty), night.Size); // laid out at the new DPI
                var rail = TestApp.Field<NavRail>(window, "_rail");
                Assert.All(rail.Items, item => Assert.Equal(Theme.Dp(38, dpi), item.Height));
                AssertNoOverlap(settings.Content);
            }
            window.Hide();
        }
        finally
        {
            perMonitor.SetValue(null, before);
        }
    });

    // ---------------------------------------------------------------- helpers

    /// <summary>Layouts per container and texts measured on this thread while it is in use.</summary>
    private sealed class Work : IDisposable
    {
        public readonly Dictionary<Control, int> Arranged = new();
        public readonly List<string> Measured = [];

        public Work()
        {
            LayoutPanel.Arranged = c => Arranged[c] = Arranged.GetValueOrDefault(c) + 1;
            // The font object itself, as the cache keys it: a regular and a bold font can share name and size.
            TextMeasure.Measured = (text, font, size, flags) =>
                Measured.Add($"「{text}」 {font.Name} {font.Style} {font.Size}px (font #{RuntimeHelpers.GetHashCode(font)}) {size} {flags}");
        }

        public int Pages => Arranged.Keys.Count(c => c is ScrollPage);

        /// <summary>No container laid out twice, no text measured twice.</summary>
        public void AssertOnce(string what, ITestOutputHelper output)
        {
            output.WriteLine($"{what}: {Arranged.Count} containers laid out, {Measured.Count} texts measured");
            var twice = Arranged.Where(kv => kv.Value > 1).Select(kv => $"{kv.Key.GetType().Name} ×{kv.Value}").ToList();
            Assert.True(twice.Count == 0, $"{what}: laid out more than once: {string.Join(", ", twice.Take(10))}");
            var again = Measured.GroupBy(m => m).Where(g => g.Count() > 1).Select(g => $"{g.Key} ×{g.Count()}").ToList();
            Assert.True(again.Count == 0, $"{what}: measured more than once: {string.Join("; ", again.Take(10))}");
        }

        public void Dispose()
        {
            LayoutPanel.Arranged = null;
            TextMeasure.Measured = null;
        }
    }

    /// <summary>An invisible window (off-screen, transparent) around a page, at a page width in 96-DPI units.</summary>
    private sealed class Host : Form
    {
        public Host(ScrollPage page, int width)
        {
            Page = page;
            Controls.Add(page);
            ShowHidden(this);
            ClientSize = new Size(Theme.Dp(width, DeviceDpi), Theme.Dp(600, DeviceDpi));
            page.SetBounds(0, 0, ClientSize.Width, ClientSize.Height);
            page.PageShown();
            page.PerformLayout();
            Application.DoEvents();
        }

        public ScrollPage Page { get; }
    }

    private static void ShowHidden(Form form)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-20000, -20000);
        form.Opacity = 0;
        form.ShowInTaskbar = false;
        form.FormBorderStyle = FormBorderStyle.None;
        form.Show();
        Application.DoEvents();
    }

    private static void Go(MainWindow window, AppPage page)
    {
        window.ShowPage(page);
        Application.DoEvents(); // what the page posted when shown (the speaker info read)
    }

    private static List<Control> MixerRows(MixerPage mixer) =>
        TestApp.Field<StackPanel>(mixer, "_rows").Controls.Cast<Control>().ToList();

    private static int Handles(Control root) =>
        (root.IsHandleCreated ? 1 : 0) + root.Controls.Cast<Control>().Sum(Handles);

    private static void AssertNoOverlap(Control root)
    {
        var shown = root.Controls.Cast<Control>().Where(c => c.Visible && c.Width > 0 && c.Height > 0).ToList();
        for (int i = 0; i < shown.Count; i++)
        for (int j = i + 1; j < shown.Count; j++)
            Assert.False(shown[i].Bounds.IntersectsWith(shown[j].Bounds), $"{shown[i].GetType().Name} {shown[i].Bounds} and {shown[j].GetType().Name} {shown[j].Bounds} overlap");
        foreach (var child in shown) AssertNoOverlap(child);
    }

    /// <summary>
    /// What Windows sends when the window moves to a monitor with another scale: WM_DPICHANGED_BEFOREPARENT to the child
    /// windows bottom-up, WM_DPICHANGED with the new rectangle (the same size: WinForms asks for it, AutoScaleMode.None), then
    /// WM_DPICHANGED_AFTERPARENT top-down.
    /// </summary>
    private static void ChangeDpi(Form window, int dpi)
    {
        var children = new List<Control>();
        void Walk(Control c)
        {
            foreach (Control child in c.Controls)
            {
                if (child.IsHandleCreated) children.Add(child);
                Walk(child);
            }
        }
        Walk(window);
        for (int i = children.Count - 1; i >= 0; i--) SendMessage(children[i].Handle, 0x02E2, dpi, IntPtr.Zero);
        var bounds = window.Bounds;
        var rect = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteInt32(rect, 0, bounds.Left);
            Marshal.WriteInt32(rect, 4, bounds.Top);
            Marshal.WriteInt32(rect, 8, bounds.Right);
            Marshal.WriteInt32(rect, 12, bounds.Bottom);
            SendMessage(window.Handle, 0x02E0, dpi | (dpi << 16), rect);
        }
        finally
        {
            Marshal.FreeHGlobal(rect);
        }
        foreach (var child in children) SendMessage(child.Handle, 0x02E3, 0, IntPtr.Zero);
    }

    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, nint wParam, IntPtr lParam);
}
