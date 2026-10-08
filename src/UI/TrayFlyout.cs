using HomePodCast.UI.Controls;

namespace HomePodCast.UI;

/// <summary>
/// The tray icon's right-click panel, Windows 11 style: speaker and connection, volume and mute, scene, night mode
/// and microphone, and the way into the main window. Borderless and rounded, in the app's theme, placed next to
/// the icon (<see cref="FlyoutPlacement"/>) at that monitor's DPI. Every change goes through the same
/// <see cref="TrayApp"/> calls as the pages, and the panel follows <see cref="TrayApp.StateChanged"/> while open.
/// Closes when it loses the focus, on Esc, or after a footer action. Tab and the arrow keys move between controls.
/// </summary>
internal sealed class TrayFlyout : Form, ISurface, ILayoutRoot
{
    private const int MinWidthDp = 340, MaxWidthDp = 380, EdgeGapDp = 12;

    private readonly TrayApp _app;
    private readonly SynchronizationContext _sync;
    private readonly ToolTip _tips = new();
    private readonly StackPanel _body = new() { Inset = new Padding(16, 16, 16, 16), Gap = 14 };
    private readonly FooterPanel _footer = new();

    // speaker
    private readonly TextBlock _name = new("", TextStyle.BodyStrong);
    private readonly StatusDot _dot = new();
    private readonly TextBlock _status = new("", TextStyle.Caption);
    private readonly FluentButton _connect = new(L.T("连接"), ButtonKind.Primary) { MinWidth = 88 };

    // volume
    private readonly FluentButton _mute = new("", ButtonKind.Subtle, Glyph.Volume);
    private readonly FluentSlider _volume = Ui.Slider(0, 100, 1, 5);
    private readonly TextBlock _volumeValue = new("", TextStyle.BodyStrong) { Align = HorizontalAlignment.Right };
    private readonly TimerDebounce _volumeWait = new(150);

    // scene, night mode, microphone
    private readonly Segmented _scenes = new(Scenes.All.Select(Scenes.Name).ToArray()) { EqualWidths = false, Compact = true };
    private readonly TextBlock _latency = new("", TextStyle.Caption, TextRole.Secondary) { Align = HorizontalAlignment.Right };
    private readonly ToggleSwitch _night = new(L.T("夜间模式"));
    private readonly ToggleSwitch _mic = new(L.T("麦克风"));

    // footer
    private readonly FluentButton _open = new(L.T("打开主界面"), ButtonKind.Subtle, Glyph.Home);
    private readonly FluentButton _settings;
    private readonly FluentButton _quit;

    private Rectangle _anchor, _work;
    private TaskbarEdge _edge;
    private bool _ready, _fitting, _closing, _busy, _closeWhenIdle;
    private bool? _shownMuted;

    public TrayFlyout(TrayApp app)
    {
        _app = app;
        _sync = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        Theme.Watch();
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.None; // sizes come from DeviceDpi (Theme.Dp)
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Text = L.T("HomePod 音响");
        BackColor = SurfaceColor;
        Font = Theme.Font(TextStyle.Body, DeviceDpi);

        _settings = Ui.IconButton(Glyph.Settings, L.T("设置"), null, _tips);
        _quit = Ui.IconButton(Glyph.Power, L.T("退出"), null, _tips);

        _body.Controls.AddRange([BuildSpeaker(), new Divider(), BuildVolume(), BuildScene(), BuildToggles()]);
        var spacer = new TextBlock();
        _footer.Controls.Add(Ui.Row(4, spacer, _open, spacer, _settings, _quit));
        Controls.Add(_body);
        Controls.Add(_footer);

        _open.Click += (_, _) => Run(() => _app.ShowMain(AppPage.Home));
        _settings.Click += (_, _) => Run(() => _app.ShowMain(AppPage.Settings));
        _quit.Click += (_, _) => Run(_app.Quit);

        _app.StateChanged += ShowState;
        Theme.Changed += OnThemeChanged;
        _ready = true;
        ResumeLayout(false);
    }

    public Color SurfaceColor => Theme.P.Popup;

    private int Dp(float value) => Theme.Dp(value, DeviceDpi);

    /// <summary>Windows 10 has no DWM border for a borderless window: the panel paints a hairline frame itself.</summary>
    private int Frame => Theme.RoundedWindows ? 0 : Math.Max(1, DeviceDpi / 96);

    // ---------------------------------------------------------------- content

    private Control BuildSpeaker()
    {
        var badge = new GlyphBadge(Glyph.Speaker, 40);
        var status = Ui.Row(6, _status, _dot, _status);
        var text = Ui.Stack(2, _name, status);
        _connect.Click += (_, _) => _app.ToggleConnection();
        return Ui.Row(12, text, badge, text, _connect);
    }

    private Control BuildVolume()
    {
        _volume.AccessibleName = L.T("音量");
        var row = Ui.Row(8, _volume, _mute, _volume, _volumeValue);
        row.FixedWidths[_volumeValue] = 32;

        _volume.Scroll += (_, _) =>
        {
            _volumeValue.Text = _volume.Value.ToString();
            _app.PreviewVolume(_volume.Value); // the speaker follows the drag; the debounce only saves
            _volumeWait.Restart();
        };
        _volume.MouseWheel += (_, e) => _volume.UserSetValue(_volume.Value + Math.Sign(e.Delta) * 2);
        _volumeWait.Tick += ApplyVolume;
        _mute.Click += (_, _) => _app.ToggleSpeakerMute();
        return row;
    }

    private Control BuildScene()
    {
        var title = new TextBlock(L.T("场景"));
        _scenes.AccessibleName = L.T("场景");
        _scenes.SelectionChangeCommitted += (_, _) =>
        {
            if (_scenes.SelectedIndex >= 0) _app.SelectScene(Scenes.All[_scenes.SelectedIndex]);
        };
        return Ui.Stack(6, Ui.Row(8, title, title, _latency), _scenes);
    }

    private Control BuildToggles()
    {
        _tips.SetToolTip(_night, L.T("压缩动态范围：爆炸、枪声变小，对白、脚步声变大，适合夜里小音量"));
        _night.Toggled += (_, _) => _app.SetNightMode(_night.Checked);
        _mic.Toggled += (_, _) => SetMic(_mic.Checked);
        return new Columns(_night, _mic) { MinColumnWidth = 120, Gap = 12 };
    }

    /// <summary>Everything from the app's current state (also while open: hotkeys, volume keys, the connection).</summary>
    private void ShowState()
    {
        if (IsDisposed) return;
        var c = _app.Controller;
        var cfg = _app.Config;

        _name.Text = cfg.DeviceName ?? L.T("还没有选择音箱");
        _dot.DotColor = Icons.For(c.State);
        _status.Text = c.StatusText;
        _status.Role = c.State switch
        {
            StreamState.Streaming => TextRole.Success,
            StreamState.Connecting => TextRole.Caution,
            StreamState.Retrying => TextRole.Critical,
            _ => TextRole.Secondary,
        };
        bool idle = c.State == StreamState.Idle;
        _connect.Text = idle ? L.T("连接") : L.T("断开");
        _connect.Kind = idle ? ButtonKind.Primary : ButtonKind.Secondary;

        int cap = cfg.VolumeCapPercent;
        _volume.Ceiling = cap;
        _volume.Mark = cap < 100 ? cap : null;
        if (!_volume.IsDragging && !_volumeWait.Enabled)
        {
            var volume = c.Volume ?? cfg.Volume;
            _volume.Value = (int)Math.Round(volume ?? 0);
            _volumeValue.Text = volume is null ? "--" : _volume.Value.ToString();
        }
        ShowMute(c.Muted);

        _scenes.SelectedIndex = Array.IndexOf(Scenes.All, cfg.Scene);
        _latency.Text = $"{_app.LatencyMs} ms";
        _night.Checked = cfg.NightMode;
        _mic.Checked = _app.Fx.MicOn;
    }

    private void ShowMute(bool muted)
    {
        _mute.Glyph = muted ? Glyph.Mute : Glyph.Volume;
        _mute.GlyphColor = muted ? Theme.P.Critical : null;
        _mute.Invalidate();
        if (_shownMuted == muted) return;
        _shownMuted = muted;
        _mute.AccessibleName = muted ? L.T("取消静音") : L.T("HomePod 静音");
        _tips.SetToolTip(_mute, _mute.AccessibleName);
    }

    private void ApplyVolume()
    {
        _volumeWait.Stop();
        _app.ApplyVolume(_volume.Value);
    }

    private void SetMic(bool on)
    {
        _busy = true; // turning it on may ask first (loudspeakers), which takes the focus away
        try
        {
            on = _app.SetMicOn(on);
        }
        finally
        {
            _busy = false;
        }
        if (IsDisposed) return;
        _mic.Checked = on;
        if (_closeWhenIdle) CloseSoon();
    }

    /// <summary>A footer action: the panel goes away first, then the action runs.</summary>
    private void Run(Action action)
    {
        _closing = true;
        Hide();
        _sync.Post(_ =>
        {
            if (!IsDisposed) Close();
            action();
        }, null);
    }

    /// <summary>Close after the current event (never from inside a control's own handler).</summary>
    private void CloseSoon()
    {
        if (_closing) return;
        _closing = true;
        _sync.Post(_ =>
        {
            if (!IsDisposed) Close();
        }, null);
    }

    // ---------------------------------------------------------------- showing and placing

    /// <summary>Open next to <paramref name="anchor"/> (the tray icon's rectangle, or the cursor).</summary>
    public void ShowAt(Rectangle anchor)
    {
        var screen = Screen.FromRectangle(anchor);
        _anchor = anchor;
        _work = screen.WorkingArea;
        _edge = FlyoutPlacement.EdgeFor(screen.Bounds, _work, anchor);

        // Create the window (and every control's) on the icon's monitor, so all of it measures at that monitor's DPI.
        Location = new Point(_work.Left + _work.Width / 2, _work.Top + _work.Height / 2);
        _ = Handle;
        CreateHandles(this);
        Font = Theme.Font(TextStyle.Body, DeviceDpi);
        ShowState();
        PerformLayout(); // sizes the panel and moves it next to the icon
        ActiveControl = _volume;
        Show();
        Activate();
        Log.Info($"tray flyout at {Bounds} ({DeviceDpi} dpi), icon {anchor}, work area {_work}, taskbar {_edge}");
    }

    private static void CreateHandles(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            _ = child.Handle;
            CreateHandles(child);
        }
    }

    private int PanelWidth()
    {
        int scenes = _scenes.GetPreferredSize(Size.Empty).Width + Dp(_body.Inset.Horizontal) + Frame * 2;
        return Math.Clamp(scenes, Dp(MinWidthDp), Dp(MaxWidthDp));
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        if (_fitting || !_ready) return; // Form setters lay out before the constructor is done
        _fitting = true;
        try
        {
            int frame = Frame, width = PanelWidth(), inner = width - frame * 2;
            int bodyHeight = _body.GetPreferredSize(new Size(inner, 0)).Height;
            int footerHeight = _footer.GetPreferredSize(new Size(inner, 0)).Height;
            var size = new Size(width, bodyHeight + footerHeight + frame * 2);
            if (ClientSize != size) ClientSize = size;
            Reposition();
            LayoutPanel.Place(_body, frame, frame, inner, bodyHeight);
            LayoutPanel.Place(_footer, frame, frame + bodyHeight, inner, footerHeight);
        }
        finally
        {
            _fitting = false;
        }
    }

    private void Reposition()
    {
        if (_work.IsEmpty) return;
        var at = FlyoutPlacement.Place(_anchor, _work, _edge, Size, Dp(EdgeGapDp), Dp(EdgeGapDp));
        if (Location != at) Location = at;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80;         // WS_EX_TOOLWINDOW: not in Alt+Tab
            cp.ClassStyle |= 0x20000;   // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.StyleFlyout(Handle);
    }

    protected override void OnPaintBackground(PaintEventArgs e) =>
        e.Graphics.Clear(Frame > 0 ? Theme.P.PopupBorder : SurfaceColor);

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Font = Theme.Font(TextStyle.Body, e.DeviceDpiNew);
        PerformLayout();
        Invalidate(true);
    }

    private void OnThemeChanged()
    {
        if (IsDisposed) return;
        BackColor = SurfaceColor;
        if (IsHandleCreated) Theme.StyleFlyout(Handle);
        ShowState();
        Invalidate(true);
    }

    // ---------------------------------------------------------------- closing and keys

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (_busy) _closeWhenIdle = true;
        else CloseSoon();
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape:
                CloseSoon();
                return true;
            case Keys.Up or Keys.Down: // the slider uses them itself; elsewhere they move like Tab
                SelectNextControl(ActiveControl, keyData == Keys.Down, tabStopOnly: true, nested: true, wrap: true);
                return true;
        }
        return base.ProcessDialogKey(keyData);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (_volumeWait.Enabled) ApplyVolume(); // a value still waiting for the debounce
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _app.StateChanged -= ShowState;
            Theme.Changed -= OnThemeChanged;
            _volumeWait.Dispose();
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>The footer band: the window colour with a hairline on top, like the Windows 11 quick settings.</summary>
    private sealed class FooterPanel : StackPanel, ISurface
    {
        public FooterPanel() => Inset = new Padding(10, 8, 10, 8);

        public Color SurfaceColor => Theme.P.Window;

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(SurfaceColor);
            using var line = new SolidBrush(Theme.P.Divider);
            e.Graphics.FillRectangle(line, 0, 0, Width, Math.Max(1, DeviceDpi / 96));
        }
    }
}
