using HomePodCast.Net;
using HomePodCast.UI.Controls;
using HomePodCast.UI.Pages;

namespace HomePodCast.UI;

internal enum AppPage { Home, Mixer, Effects, Settings }

/// <summary>
/// The one window: a navigation rail (首页 / 混音器 / 麦克风与音效 / 设置) and the pages. Light or dark
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
    private readonly SettingsPage _settings;
    private readonly ScrollPage[] _pages;
    private int _current;
    private ScrollPage? _shown;

    public MainWindow(TrayApp app)
    {
        _app = app;
        Theme.Watch();
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.None; // sizes come from DeviceDpi (Theme.Dp), never from AutoScale
        Text = "HomePodCast";
        Icon = Icons.Speaker(Icons.Streaming);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.P.Window;
        Font = Theme.Font(TextStyle.Body, DeviceDpi);

        _rail.Add(Glyph.Home, L.T("首页"));
        _rail.Add(Glyph.Mixer, L.T("混音器"));
        _rail.Add(Glyph.Microphone, L.T("麦克风与音效"));
        _rail.Add(Glyph.Settings, L.T("设置"));
        _rail.Navigate += i => ShowPage((AppPage)i);

        _home = new HomePage(app);
        _mixer = new MixerPage(app);
        _effects = new EffectsPage(app.Config, app.Fx);
        _settings = new SettingsPage(app);
        _pages = [_home, _mixer, _effects, _settings];
        foreach (var page in _pages)
        {
            page.Visible = false;
            Controls.Add(page);
        }
        Controls.Add(_rail);
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
        MinimumSize = new Size(Math.Min(Dp(700), w), Math.Min(Dp(460), h));
    }

    // ---------------------------------------------------------------- pages

    public void ShowPage(AppPage page)
    {
        SelectPage(page);
        UpdateShownPage();
    }

    private void SelectPage(AppPage page)
    {
        int index = (int)page;
        _rail.SelectedIndex = index;
        if (index == _current && _pages[index].Visible) return;
        _current = index;
        for (int i = 0; i < _pages.Length; i++) _pages[i].Visible = i == index;
        PerformLayout();
    }

    /// <summary>Tell pages when they become visible (timers, mixer sessions) or stop being visible.</summary>
    private void UpdateShownPage()
    {
        var want = Visible && WindowState != FormWindowState.Minimized ? _pages[_current] : null;
        if (want == _shown) return;
        _shown?.PageHidden();
        _shown = want;
        _shown?.PageShown();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdateShownPage();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateShownPage();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        if (_pages is null) return; // Form setters lay out before the constructor is done
        var client = ClientSize;
        int rail = Math.Min(_rail.PreferredWidth(), Math.Max(Dp(160), client.Width / 3));
        LayoutPanel.Place(_rail, 0, 0, rail, client.Height);
        var page = _pages[_current];
        if (page.Left != rail || page.Width != client.Width - rail || page.Height != client.Height)
            page.SetBounds(rail, 0, Math.Max(0, client.Width - rail), client.Height);
        page.PerformLayout();
    }

    // ---------------------------------------------------------------- theme, DPI, closing

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.StyleWindow(this);
    }

    private void OnThemeChanged()
    {
        if (IsDisposed) return;
        BackColor = Theme.P.Window;
        Theme.StyleWindow(this);
        Invalidate(true);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Font = Theme.Font(TextStyle.Body, e.DeviceDpiNew);
        MinimumSize = new Size(Theme.Dp(700, e.DeviceDpiNew), Theme.Dp(460, e.DeviceDpiNew));
        PerformLayout();
        Invalidate(true);
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
        if (disposing) Theme.Changed -= OnThemeChanged;
        base.Dispose(disposing);
    }

    // ---------------------------------------------------------------- what TrayApp tells the window

    public void SetDevices(List<AirPlayDevice> devices) => _home.SetDevices(devices);
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
    }

    /// <summary>An output device was added, removed, enabled, disabled, or the default changed.</summary>
    public void AudioDevicesChanged() => _mixer.AudioDevicesChanged();

    public void ShowSoundOptions()
    {
        _home.ShowSoundOptions();
        _mixer.ShowSoundOptions();
        _settings.ShowSoundOptions();
    }

    public void ShowVolume(double percent)
    {
        _home.ShowVolume(percent);
        _mixer.ShowVolume(percent);
    }

    /// <summary>What the volume keys do changed (mode, stream, output device): 设置 says so.</summary>
    public void ShowVolumeKeys() => _settings.ShowVolumeKeys();

    /// <summary>Save what is still waiting for a debounce (before the app quits).</summary>
    public void Flush() => _effects.Flush();
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
        Icon = Icons.Speaker(Icons.Streaming);
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
