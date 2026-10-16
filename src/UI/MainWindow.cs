using HomePodCast.Net;
using HomePodCast.UI.Controls;
using HomePodCast.UI.Pages;

namespace HomePodCast.UI;

internal enum AppPage { Home, Mixer, Effects, Equalizer, Speakers, Group, Viewing, Sync, Profiles, Settings, About }

/// <summary>
/// The one window: a scrolling navigation rail, fixed theme/settings/about actions and focused pages. Light or dark
/// with the system (or the "Theme" setting), dark title bar and Mica where Windows has them. Everything
/// is laid out in code for the window's DPI. Closing only hides it; the tray flyout quits.
/// </summary>
internal sealed class MainWindow : Form, ISurface
{
    private readonly TrayApp _app;
    private readonly NavRail _rail = new();
    private readonly HomePage _home;
    private readonly MixerPage _mixer;
    private readonly EffectsPage _effects;
    private readonly EqualizerPage _equalizer;
    private readonly SettingsPage _settings;
    private readonly AboutPage _about;
    private readonly ProfilesPage _profiles;
    private readonly SpeakersPage _speakers;
    private readonly GroupPage _group;
    private readonly ScrollPage[] _pages;
    private readonly ThemeTransition _transition = new();
    private readonly System.Windows.Forms.Timer _dpiSettle = new() { Interval = DpiSettleMs };
    private readonly System.Windows.Forms.Timer _prepare = new() { Interval = PrepareStartMs };
    private int _current;
    private ScrollPage? _shown;
    private bool _sizeMove, _dpiLayoutPending;
    private int _prepareNext;
    private long _lastInput;
    private bool _watchingInput;

    public MainWindow(TrayApp app)
    {
        _app = app;
        Theme.Watch();
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.None; // sizes come from DeviceDpi (Theme.Dp), never from AutoScale
        Text = "HomePodCast";
        Icon = Icons.App;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.P.Window;
        Font = Theme.Font(TextStyle.Body, DeviceDpi);

        _rail.Add(Glyph.Home, L.T("首页"));
        _rail.Add(Glyph.Mixer, L.T("混音器"));
        _rail.Add(Glyph.Microphone, L.T("麦克风"));
        _rail.Add(Glyph.Equalizer, L.T("均衡"));
        _rail.Add(Glyph.Speaker, L.T("音箱信息"));
        _rail.Add(Glyph.Devices, L.T("多音箱（实验性）"), label: L.T("多音箱")); // experimental since 1.1; hardware untested
        _rail.Add(Glyph.Screen, L.T("同步观影窗口（实验性）"), label: L.T("同步观影窗口")).Collapsed = !Players.VideoWindowModule.Offered; // experimental since 1.1
        _rail.Add(Glyph.Gauge, L.T("音画同步测试"));
        _rail.Add(Glyph.Profiles, L.T("使用配置"));
        _rail.Add(Glyph.Settings, L.T("设置"), bottom: true);
        _rail.Add(Glyph.Info, L.T("关于"), bottom: true);
        _rail.ThemeRequested += ToggleTheme;
        _rail.Navigate += i => ShowPage((AppPage)i);

        _home = new HomePage(app);
        _mixer = new MixerPage(app);
        _effects = new EffectsPage(app.Config, app.Fx);
        _equalizer = new EqualizerPage(app.Config, app.Fx);
        _settings = new SettingsPage(app);
        _about = new AboutPage(app);
        _profiles = new ProfilesPage(app);
        _speakers = new SpeakersPage(app);
        _group = new GroupPage(app);
        _pages = [_home, _mixer, _effects, _equalizer, _speakers, _group, new ViewingPage(app), new SyncPage(app), _profiles, _settings, _about];
        foreach (var page in _pages)
        {
            page.Visible = false;
            Controls.Add(page);
        }
        Controls.Add(_rail);
        Controls.Add(_transition);
        _rail.TabIndex = 0;
        for (int i = 0; i < _pages.Length; i++) _pages[i].TabIndex = 1 + i;

        _home.VolumeApplied += v => _mixer.ShowVolume(v);
        _mixer.VolumeApplied += v => _home.ShowVolume(v);
        _settings.CapChanged += () =>
        {
            _home.ShowSoundOptions();
            _mixer.ShowSoundOptions();
            var volume = _app.Controller.Volume ?? _app.Config.Volume;
            if (volume is { } v)
            {
                _home.ShowVolume(v);
                _mixer.ShowVolume(v);
            }
        };

        SetInitialSize();
        SelectPage(AppPage.Home);
        Theme.Changed += OnThemeChanged;
        _dpiSettle.Tick += (_, _) => { if (!_sizeMove) FinishDpiChange(); }; // in a drag: when it ends (WndProc)
        _prepare.Tick += (_, _) => PrepareSome();
        ResumeLayout(false);
    }

    public Color SurfaceColor => Theme.P.Window;
    public AppPage CurrentPage => (AppPage)_current;

    private int Dp(float v) => Theme.Dp(v, DeviceDpi);

    private void SetInitialSize()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        int w = Math.Min(Dp(1000), area.Width * 94 / 100), h = Math.Min(Dp(700), area.Height * 92 / 100);
        Size = new Size(w, h);
        MinimumSize = new Size(Math.Min(Dp(600), w), Math.Min(Dp(460), h));
    }

    // ---------------------------------------------------------------- pages

    public void ShowPage(AppPage page)
    {
        if (!_sizeMove) FinishDpiChange(); // shown right after a move to another monitor: laid out at its DPI first
        using var layout = LayoutPanel.BatchLayout(this);
        _transition.Stop();
        SelectPage(page);
        UpdateShownPage();
    }

    private void SelectPage(AppPage page)
    {
        int index = (int)page;
        _rail.SelectedIndex = index;
        if (index == _current && _pages[index].Visible) return;
        using var layout = LayoutPanel.BatchLayout(this);
        _current = index;
        for (int i = 0; i < _pages.Length; i++) _pages[i].Visible = i == index;
    }

    /// <summary>Tell pages when they become visible (timers, mixer sessions) or stop being visible.</summary>
    private void UpdateShownPage()
    {
        var want = Visible && WindowState != FormWindowState.Minimized ? _pages[_current] : null;
        if (want != null) StartPreparing();
        if (want == _shown) return;
        _shown?.PageHidden();
        _shown = want;
        _shown?.PageShown();
    }

    // ---------------------------------------------------------------- pages built ahead of their first visit

    /// <summary>First step this long after the window is shown; then one step each <see cref="PrepareStepMs"/> at most.</summary>
    internal const int PrepareStartMs = 400, PrepareStepMs = 30;

    /// <summary>Work per step, in ms: a window takes about 1.4 ms to create on the maintainer's PC.</summary>
    private const int PrepareBudgetMs = 8;

    /// <summary>The user used the window this recently (ms): no step yet.</summary>
    private const int PrepareQuietMs = 400;

    /// <summary>Every page has been built and laid out ahead, or visited.</summary>
    internal bool PagesPrepared => _prepareNext >= _pages.Length;

    /// <summary>Tests: false leaves the pages to their first visit, as before (counting the work of a page never shown).</summary>
    internal bool PrepareAhead { get; set; } = true;

    /// <summary>
    /// A page shown for the first time created all its windows (about 1.4 ms each on the maintainer's PC: 100 for 混音器 or
    /// 麦克风与音效) and laid itself out, 100–250 ms before it appeared (1.1.0-beta.7). Once the window is shown (never while
    /// only the tray icon is), the other pages are built ahead instead, one after the other, a few milliseconds at a time
    /// while the window is idle: a timer (its messages come only when no input or other message waits), and nothing while the
    /// user clicks, types, moves the mouse over the window or drags it. Hidden meanwhile, it stops until shown again.
    /// </summary>
    private void StartPreparing()
    {
        if (!PrepareAhead || PagesPrepared || _prepare.Enabled || IsDisposed) return;
        _prepare.Interval = PrepareStartMs;
        _prepare.Start();
        if (!_watchingInput)
        {
            Application.AddMessageFilter(_inputWatch ??= new InputWatch(this));
            _watchingInput = true;
        }
    }

    private void StopPreparing()
    {
        _prepare.Stop();
        if (_watchingInput)
        {
            Application.RemoveMessageFilter(_inputWatch!);
            _watchingInput = false;
        }
    }

    private InputWatch? _inputWatch;

    /// <summary>When this thread last got keyboard or mouse input (any of its windows).</summary>
    private sealed class InputWatch(MainWindow owner) : IMessageFilter
    {
        public bool PreFilterMessage(ref Message m)
        {
            // WM_KEYFIRST…WM_KEYLAST, WM_NCMOUSEMOVE…WM_NCXBUTTONDBLCLK, WM_MOUSEFIRST…WM_MOUSELAST
            if (m.Msg is >= 0x0100 and <= 0x0109 or >= 0x00A0 and <= 0x00AD or >= 0x0200 and <= 0x020E)
                owner._lastInput = Environment.TickCount64;
            return false;
        }
    }

    private bool UserBusy() =>
        _sizeMove || _dpiLayoutPending || _transition.Running || MouseButtons != MouseButtons.None || InputWaiting() ||
        Environment.TickCount64 - _lastInput < PrepareQuietMs;

    private static bool InputWaiting() => (GetQueueStatus(0x0407) >> 16) != 0; // QS_KEY | QS_MOUSE | QS_RAWINPUT

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetQueueStatus(uint flags);

    /// <summary>One step of <see cref="StartPreparing"/>.</summary>
    private void PrepareSome()
    {
        if (_prepare.Interval != PrepareStepMs) _prepare.Interval = PrepareStepMs;
        // Also turned off while the timer still ran: the step that finished the last page leaves one more tick.
        if (!PrepareAhead || !Visible || WindowState == FormWindowState.Minimized)
        {
            StopPreparing(); // UpdateShownPage starts it again
            return;
        }
        if (UserBusy()) return;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        bool Yield() => watch.ElapsedMilliseconds >= PrepareBudgetMs || InputWaiting();
        try
        {
            for (; _prepareNext < _pages.Length; _prepareNext++)
            {
                var page = _pages[_prepareNext];
                // The shown page, and one shown before (Created: its first visit did this), need nothing.
                if (_prepareNext == _current || page.Created || page.IsDisposed) continue;
                if (!Prepare(page, Yield)) return;
                if (Yield())
                {
                    _prepareNext++;
                    return;
                }
            }
            StopPreparing();
        }
        finally
        {
            PrepareSteps++;
            LongestPrepareStepMs = Math.Max(LongestPrepareStepMs, watch.ElapsedMilliseconds);
        }
    }

    /// <summary>Tests: steps taken by <see cref="StartPreparing"/>, and the longest one.</summary>
    internal int PrepareSteps { get; private set; }
    internal long LongestPrepareStepMs { get; private set; }

    /// <summary>
    /// Build the hidden <paramref name="page"/> as its first visit would (what it builds when shown, its windows) and lay it
    /// out where it will show, at this DPI; false when <paramref name="yield"/> stopped it (the next call goes on).
    /// </summary>
    private bool Prepare(ScrollPage page, Func<bool> yield)
    {
        // WinForms lays a parent out when a child's bounds change or a child window is created. Nothing here changes the rail
        // or the shown page, and the page is laid out below: those layouts are dropped (2026-10-15: 首页 25 times otherwise).
        SuspendLayout();
        try
        {
            var area = PageArea();
            if (!page.IsHandleCreated)
            {
                // Not yet a window, and its controls (no windows either) already at this window's DPI: laid out first, so that
                // their windows are created where they belong. Moving windows is most of a layout's time on the maintainer's PC.
                bool layoutFirst = page.DeviceDpi == DeviceDpi;
                using (layoutFirst ? LayoutPanel.BatchLayout(page) : LayoutPanel.SuspendLayouts(page))
                {
                    page.Prepare();
                    page.ApplyFont();
                    if (layoutFirst) page.Bounds = area;
                }
                using (LayoutPanel.SuspendLayouts(page)) _ = page.Handle; // the next step goes on with the windows in it
                if (yield()) return false;
            }
            using (LayoutPanel.SuspendLayouts(page))
            {
                if (!LayoutPanel.CreateWindows(page, yield)) return false;
            }
            page.ApplyFont();
            // Windows created first (their DPI is known once they exist), or the window changed size or DPI meanwhile.
            if (page.Bounds != area || page.LayoutDpi != DeviceDpi) LayoutPanel.Place(page, area.X, area.Y, area.Width, area.Height);
            return true;
        }
        finally
        {
            ResumeLayout(false);
        }
    }

    /// <summary>Where the pages go: right of the navigation rail.</summary>
    private Rectangle PageArea()
    {
        var client = ClientSize;
        int rail = _rail.WidthForWindow(client.Width);
        return new Rectangle(rail, 0, Math.Max(0, client.Width - rail), client.Height);
    }

    /// <summary>
    /// Shown, its windows created and laid out once: each window created asked its container for a layout (2026-10-15, the
    /// window shown from the tray for the first time: 163 layouts, rows of 首页 9 times each).
    /// </summary>
    protected override void SetVisibleCore(bool value)
    {
        if (!value || _pages is null || Visible) base.SetVisibleCore(value);
        else using (LayoutPanel.BatchLayout(this)) base.SetVisibleCore(value);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        if (!Visible) _transition?.Stop();
        base.OnVisibleChanged(e);
        UpdateShownPage();
    }

    protected override void OnResize(EventArgs e)
    {
        _transition?.Stop();
        base.OnResize(e);
        UpdateShownPage();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        if (_pages is null) return; // Form setters lay out before the constructor is done
        bool dpiChanged = _dpiLayoutPending;
        _dpiLayoutPending = false;
        var area = PageArea();
        LayoutPanel.Place(_rail, 0, 0, area.X, area.Height);
        LayoutPanel.Place(_pages[_current], area.X, area.Y, area.Width, area.Height);
        if (dpiChanged) Invalidate(true);
    }

    // ---------------------------------------------------------------- theme, DPI, closing

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.StyleWindow(this);
    }

    internal void ToggleTheme()
    {
        _transition.Run(this, () =>
        {
            _app.Config.Theme = Theme.IsDark ? Theme.Light : Theme.Dark;
            _app.Config.Save();
            Theme.Mode = _app.Config.Theme;
            _settings.ShowTheme();
        });
    }

    private void OnThemeChanged()
    {
        if (IsDisposed) return;
        BackColor = Theme.P.Window;
        Theme.StyleWindow(this);
        Invalidate(true);
    }

    /// <summary>
    /// A move to a monitor with another scale: the new font and sizes now (the rail and the shown page; hidden pages when they
    /// are shown, ScrollPage.OnParentFontChanged), the layout once the window has settled at its DPI, when the drag ends or
    /// <see cref="DpiSettleMs"/> after the last change otherwise. A drag back and forth over the edge between the monitors
    /// changes the DPI each time: laid out each time, it stopped following the mouse for every one (1.1.0-beta.7).
    /// </summary>
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        BeginDpiChange();
        using (LayoutPanel.SuspendLayouts(this))
        {
            base.OnDpiChanged(e);
            Font = Theme.Font(TextStyle.Body, e.DeviceDpiNew);
            MinimumSize = new Size(Theme.Dp(600, e.DeviceDpiNew), Theme.Dp(460, e.DeviceDpiNew));
        }
    }

    /// <summary>
    /// Every layout in the window held from the start of a DPI change to <see cref="FinishDpiChange"/>. Windows asks the window
    /// for its size at the new DPI (WM_GETDPISCALEDSIZE) before it tells the child windows (WM_DPICHANGED_BEFOREPARENT), and it
    /// moves each child window as it tells it: each page and container moved laid itself out, and the window too, once per
    /// page, at the old DPI; then the window's frame changed at the new DPI and its client area with it, another layout.
    /// 2026-10-15, the maintainer's monitors (150 % and 100 %): 1.1–2.3 s for a move across, 0.9 s more with 混音器 shown.
    /// </summary>
    private void BeginDpiChange()
    {
        _transition.Stop();
        if (_dpiHold == null)
        {
            ReleaseHiddenPages();
            _dpiHold = LayoutPanel.SuspendLayouts(this);
        }
        _dpiLayoutPending = true;
        _dpiSettle.Stop();
        _dpiSettle.Start();
    }

    private IDisposable? _dpiHold;

    /// <summary>
    /// Windows tells every child window of a DPI change and moves it, about 2.2 ms per window on the maintainer's PC, hidden
    /// or not: with the pages built ahead (445 windows) a move across took 0.8–1.1 s even with every layout held, 0.2 s with
    /// only the rail and 首页 (70). The hidden pages' windows go first (20 ms for all of them) and are built again at idle,
    /// at the new DPI (<see cref="StartPreparing"/> after <see cref="FinishDpiChange"/>).
    /// </summary>
    private void ReleaseHiddenPages()
    {
        foreach (var page in _pages)
            if (page != _pages[_current]) page.ReleaseWindows();
        _prepareNext = 0;
    }

    /// <summary>Quiet time after a DPI change before the window is laid out at it (outside a drag).</summary>
    internal const int DpiSettleMs = 150;

    /// <summary>A DPI change waits for its layout (<see cref="OnDpiChanged"/>).</summary>
    internal bool DpiLayoutPending => _dpiLayoutPending;

    /// <summary>The layout a DPI change left for later, now (also before another page is shown meanwhile).</summary>
    internal void FinishDpiChange()
    {
        _dpiSettle.Stop();
        if (!_dpiLayoutPending || IsDisposed) return;
        _dpiHold?.Dispose();
        _dpiHold = null;
        // The rail's and the page's windows moved without drawing each one, then drawn once. 2026-10-16, on the maintainer's
        // monitors: the layout after a move across took 130–550 ms with each window drawn as it moved, 100–330 ms without.
        Control[] shown = [.. new Control[] { _rail, _pages[_current] }.Where(c => c.IsHandleCreated)];
        foreach (var c in shown) SendMessage(c.Handle, WM_SETREDRAW, 0, 0);
        try
        {
            using (LayoutPanel.BatchLayout(this)) { } // OnLayout
        }
        finally
        {
            foreach (var c in shown) SendMessage(c.Handle, WM_SETREDRAW, 1, 0);
            if (IsHandleCreated) RedrawWindow(Handle, 0, 0, RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN);
        }
        if (_dpiLayoutPending) _dpiSettle.Start(); // the window's layout was held (a batch in progress): again later
        else if (Visible) StartPreparing(); // the hidden pages again, at this DPI
    }

    private const int WM_SETREDRAW = 0x000B;
    private const uint RDW_INVALIDATE = 0x0001, RDW_ERASE = 0x0004, RDW_ALLCHILDREN = 0x0080, RDW_FRAME = 0x0400;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, nint wParam, nint lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr region, uint flags);

    protected override void WndProc(ref Message m)
    {
        const int WM_ENTERSIZEMOVE = 0x0231, WM_EXITSIZEMOVE = 0x0232, WM_GETDPISCALEDSIZE = 0x02E4;
        if (m.Msg == WM_ENTERSIZEMOVE) _sizeMove = true;
        if (m.Msg == WM_GETDPISCALEDSIZE) BeginDpiChange();
        base.WndProc(ref m);
        if (m.Msg == WM_EXITSIZEMOVE)
        {
            _sizeMove = false;
            FinishDpiChange();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true; // closing the window only hides it; quit from the tray flyout
            Hide();
            _app.ShowHiddenHint();
            return;
        }
        base.OnFormClosing(e);
        // Shutdown/logoff, or an installer's Restart Manager / Task Manager closing us (WM_QUERYENDSESSION or an
        // external WM_CLOSE): leave the whole app properly — stop streaming, restore per-app volumes and player
        // delays — instead of leaving a tray process that Windows then has to kill.
        if (e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing) _app.QuitForSystem();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= OnThemeChanged;
            _transition.Dispose();
            StopPreparing();
            _prepare.Dispose();
            _dpiSettle.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---------------------------------------------------------------- what TrayApp tells the window

    public void SetDevices(List<AirPlayDevice> devices) { _home.SetDevices(devices); _speakers.RefreshDevices(); if (CurrentPage == AppPage.Group) _group.RefreshSpeakers(); }
    public void SetScanning(bool scanning) => _home.SetScanning(scanning);
    public void SelectScene(Scene scene) => _home.SelectScene(scene);
    public void ShowHotkeyStatus(int unavailable) => _settings.ShowHotkeyStatus(unavailable);
    public bool SetMicOn(bool on) => _effects.SetMicOn(on);
    public void ShowPlayers()
    {
        _home.ShowPlayers();
        _settings.ShowPlayers();
    }

    /// <summary>The latency 首页 shows (the scene's, or the slider's while it waits to reconnect).</summary>
    public int LatencyMs => _home.LatencyMs;

    /// <summary>The scene or the latency changed (buttons, slider, hotkey, tray flyout).</summary>
    public event Action? LatencyChanged
    {
        add => _home.LatencyChanged += value;
        remove => _home.LatencyChanged -= value;
    }

    public void UpdateState()
    {
        _home.UpdateState();
        _mixer.UpdateState();
        _speakers.RefreshDevices();
    }

    /// <summary>An output device was added, removed, enabled, disabled, or the default changed.</summary>
    public void AudioDevicesChanged() => _mixer.AudioDevicesChanged();

    public void ShowSoundOptions()
    {
        _home.ShowSoundOptions();
        _mixer.ShowSoundOptions();
        _settings.ShowSoundOptions();
        _equalizer.ShowNightMode();
    }

    public void ShowVolume(double percent)
    {
        _home.ShowVolume(percent);
        _mixer.ShowVolume(percent);
    }

    /// <summary>What the volume keys do changed (mode, stream, output device): 设置 says so.</summary>
    public void ShowVolumeKeys() => _settings.ShowVolumeKeys();

    /// <summary>The update check, download or result changed: 设置 → 软件更新 and the banner on 首页.</summary>
    public void ShowUpdate()
    {
        _home.ShowUpdate();
        _about.ShowUpdate();
    }

    /// <summary>The .NET runtime this copy started on was found gone: 首页's restart notice.</summary>
    public void ShowRuntimeNotice() => _home.ShowRuntimeNotice();

    /// <summary>Save what is still waiting for a debounce (before the app quits).</summary>
    public void Flush()
    {
        _home.FlushPendingVolume();
        _mixer.FlushPendingVolume();
        _effects.Flush();
        _equalizer.Flush();
        _settings.FlushPendingSettings();
        _group.Flush();
    }

    public void FlushLatency() => _home.FlushLatency();
    public void CancelPendingLatency() => _home.CancelPendingLatency();
    public void ShowProfiles() { _home.ShowProfiles(); _profiles.Reload(); }
    public void ReloadConfiguration()
    {
        _home.ReloadConfiguration();
        _mixer.AudioDevicesChanged();
        _mixer.UpdateState();
        _effects.ReloadConfiguration();
        _equalizer.ReloadConfiguration();
        _settings.ReloadConfiguration();
        _profiles.Reload();
        _speakers.RefreshDevices();
        ShowSoundOptions();
    }
}

/// <summary>A themed window around a single page (<c>gui --effects</c>).</summary>
internal sealed class PageWindow : Form, ISurface
{
    private readonly ScrollPage _page;

    public PageWindow(ScrollPage page, string title)
    {
        _page = page;
        Theme.Watch();
        AutoScaleMode = AutoScaleMode.None;
        Text = title;
        Icon = Icons.App;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.P.Window;
        Font = Theme.Font(TextStyle.Body, DeviceDpi);
        Controls.Add(page);
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Size = new Size(Math.Min(Theme.Dp(820, DeviceDpi), area.Width * 9 / 10), Math.Min(Theme.Dp(720, DeviceDpi), area.Height * 9 / 10));
        Theme.Changed += OnThemeChanged;
    }

    public Color SurfaceColor => Theme.P.Window;

    private void OnThemeChanged()
    {
        if (IsDisposed) return;
        BackColor = Theme.P.Window;
        Theme.StyleWindow(this);
        Invalidate(true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.StyleWindow(this);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        _page.SetBounds(0, 0, ClientSize.Width, ClientSize.Height);
        _page.PerformLayout();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _page.PageShown();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _page.PageHidden();
        Theme.Changed -= OnThemeChanged;
        base.OnFormClosed(e);
    }
}
