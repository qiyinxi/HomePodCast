using HomePodCast.Audio;
using HomePodCast.UI.Controls;

namespace HomePodCast.UI.Pages;

/// <summary>
/// 混音器: the speaker's volume, which output device is captured (采集设备: the Windows default output, or a
/// device of the user's choice for routing by sound card), where apps without their own rule go (HomePod /
/// 本机 / 两者) and what routing does now, and one bordered row per app on the captured device with a live
/// level meter, its destination, volume and mute. App rows are created while the page is visible and kept, without
/// their audio sessions, while it is hidden; everything is sized for the page's DPI.
/// </summary>
internal sealed partial class MixerPage : ScrollPage
{

    /// <summary>
    /// With a chosen capture device, 「HomePod 跟随 Windows 音量」 follows that device's volume (VolumeKeyForwarder watches
    /// it), while the keyboard keys change the default output. Shown here and under 键盘音量键 in 设置 (L.T).
    /// </summary>
    public const string CaptureVolumeNote =
        "指定了采集设备时，「HomePod 跟随 Windows 音量」跟随的是这块设备的音量；音量键只调 Windows 默认输出。想用音量键调 HomePod，就把这块设备设为默认输出。";

    private readonly TrayApp _app;
    private readonly FluentComboBox _captureDevice = new() { MinWidth = 220 };
    private readonly SettingRow _captureRow;
    private List<AudioEndpoint> _captureOutputs = [];
    private readonly ToolTip _tips = new();
    private readonly System.Windows.Forms.Timer _meterTimer = new() { Interval = 50 };
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 1500 };

    private readonly TextBlock _masterTitle = Ui.Header("");
    private readonly TextBlock _masterValue = new("", TextStyle.BodyStrong) { Align = HorizontalAlignment.Right };
    private readonly FluentSlider _master = Ui.Slider(0, 100, 1, 5);
    private readonly TimerDebounce _masterWait = new(150);

    private readonly FluentComboBox _routeDefault = new() { MinWidth = 230, PreferredWidth = 290 };
    private readonly SettingRow _routeRow;

    private readonly StackPanel _rows = new() { Gap = 4 };
    private readonly TextBlock _empty = new(L.T("现在没有程序在发声。"), TextStyle.Body, TextRole.Secondary);
    private readonly Dictionary<string, AppRow> _byKey = new();
    private List<AppAudio> _apps = [];
    private bool _active;

    public MixerPage(TrayApp app, bool logicalLayout = true) : base(L.T("混音器"))
    {
        _app = app;
        _master.AccessibleName = L.T("音量");
        var masterHead = Ui.Row(8, _masterTitle, _masterTitle, _masterValue);
        var masterCard = Ui.Card(masterHead, _master);
        masterCard.Gap = 6;

        _captureDevice.AccessibleName = L.T("采集设备");
        _captureRow = new SettingRow(Glyph.Speaker, L.T("采集设备"), "", _captureDevice);
        _captureRow.DescriptionText.Wrap = true;

        _routeDefault.Enabled = AppRouting.Supported;
        _routeRow = new SettingRow(Glyph.Devices, L.T("没单独设置的程序送到"), L.T("适用于未单独设置输出目标的应用。"), _routeDefault);
        _routeRow.DescriptionText.Wrap = true;

        var modes = new MixerModeHelp();

        var appsHeader = Ui.Section(L.T("应用（推送到音箱的声音）"));
        var note = Ui.Note(L.F("Windows 音量和静音与系统共用。开启独立音量后，可单独调节程序送往音箱的音量；开启时不会超过当前 Windows 音量。远端加本机不承诺两处同步。本机播放不建立额外副本；系统提示音在按程序采集时只留在本机。独立音量也会增加约 {0} ms 采集延迟。", RoutedCapture.RoutedExtraLatencyMs));
        Content.Controls.AddRange([masterCard, _captureRow, _routeRow, modes, appsHeader, _rows, _empty, note]);
        InitializeDestinations(modes);
        Content.GapBefore[appsHeader] = 20;
        Content.GapBefore[_rows] = 8;
        // _empty stays shown until the first refresh (PageShown, before the page is painted): a page built ahead of its first
        // visit (MainWindow) then has its window, and finding no app creates none.

        _master.Scroll += (_, _) =>
        {
            _masterValue.Text = _master.Value.ToString();
            _app.PreviewVolume(_master.Value); // the speaker follows the drag; the debounce only saves
            _masterWait.Restart();
        };
        _masterWait.Tick += () =>
        {
            _masterWait.Stop();
            _app.SetVolume(_master.Value);
            VolumeApplied?.Invoke(_master.Value);
        };
        _routeDefault.SelectionChangeCommitted += async (_, _) => await ApplyDefaultChoice();
        _captureDevice.SelectionChangeCommitted += (_, _) => OnCaptureDeviceChosen();
        _meterTimer.Tick += (_, _) => UpdateMeters();
        _refreshTimer.Tick += (_, _) =>
        {
            RefreshApps();
            SyncRoutes();
        };
        ShowMaster();
        if (logicalLayout) LogicalLayout.Enable(Content); // tests compare both hierarchies on the same fake page
    }

    // ---------------------------------------------------------------- capture device

    /// <summary>
    /// 跟随 Windows 默认输出, then every active output; a chosen device that is gone stays listed as unavailable
    /// (it is still what gets captured once it is back; nothing falls back silently).
    /// </summary>
    private void LoadCaptureDevices()
    {
        if (_captureDevice.DroppedDown) return; // the next device change or page visit refreshes it
        _captureOutputs = FindCaptureOutputs();
        var chosen = _app.Routing.CaptureDeviceId;
        _captureDevice.Items.Clear();
        _captureDevice.Items.Add(L.T("跟随 Windows 默认输出（推荐）"));
        foreach (var d in _captureOutputs) _captureDevice.Items.Add(d.Name);
        int index = _captureOutputs.FindIndex(d => CaptureEndpoint.Same(d.Id, chosen));
        if (index < 0 && chosen != null)
        {
            _captureDevice.Items.Add(L.F("{0}（不可用）", ChosenName()));
            _captureDevice.SelectedIndex = _captureDevice.Items.Count - 1;
        }
        else
        {
            _captureDevice.SelectedIndex = index + 1;
        }
        ShowCaptureStatus();
    }

    private string ChosenName()
    {
        var chosen = _app.Routing.CaptureDeviceId;
        return _captureOutputs.FirstOrDefault(d => CaptureEndpoint.Same(d.Id, chosen))?.Name
               ?? _app.Config.CaptureDeviceName ?? chosen ?? "";
    }

    private void OnCaptureDeviceChosen()
    {
        int i = _captureDevice.SelectedIndex;
        if (i <= 0) _app.SetCaptureDevice(null, null);
        else if (i - 1 < _captureOutputs.Count) _app.SetCaptureDevice(_captureOutputs[i - 1].Id, _captureOutputs[i - 1].Name);
        else return; // the unavailable entry: already chosen
        LoadCaptureDevices();
        if (!_active) return;
        ClearRows(); // the rows belong to the previous device's sessions, even for the same programs
        RefreshApps();
        SyncRoutes();
    }

    /// <summary>What the chosen device means now; a caution while a chosen device is unavailable.</summary>
    private void ShowCaptureStatus()
    {
        var chosen = _app.Routing.CaptureDeviceId;
        var desc = _captureRow.DescriptionText;
        bool missing = chosen != null && !_captureOutputs.Any(d => CaptureEndpoint.Same(d.Id, chosen));
        desc.Text = chosen == null ? L.T("推送 Windows 默认输出上的所有声音；默认输出改变时自动跟随。")
            : missing ? L.F("「{0}」现在不可用（已拔出、停用或卸载）：不会改用默认输出，设备恢复后自动继续。", ChosenName())
            : L.F("只推送「{0}」上的声音；其他设备上的程序（比如耳机里的语音聊天）不会被推送。", ChosenName()) +
              (_app.EffectiveVolumeKeys == VolumeKeyMode.FollowWindows ? "\n" + L.T(CaptureVolumeNote) : "");
        desc.Role = missing ? TextRole.Caution : TextRole.Secondary;
    }

    /// <summary>An output device came, went, or changed state (TrayApp, from the device notifications).</summary>
    public void AudioDevicesChanged()
    {
        if (!_active) return; // PageShown lists them afresh
        LoadCaptureDevices();
        RefreshApps();
    }

    /// <summary>The volume was changed here (首页 follows).</summary>
    public event Action<double>? VolumeApplied;

    private string DefaultRouteText() => L.F("跟随默认：{0}", MixerDestinationOptions.Name(_app.Config,
        MixerRoutes.Read(_app.Config).Settings?.Default ?? MixerRoute.Current, _app.LastScan));

    // ---------------------------------------------------------------- speaker volume

    private void ShowMaster()
    {
        _masterTitle.Text = L.F("{0} 音量", _app.Mixer?.Statuses.FirstOrDefault(s => s.Key == _app.SelectedMixerKey)?.Name ?? _app.Config.DeviceName ?? "HomePod");
        _master.Enabled = !_app.NativeMixerView;
        int cap = _app.Config.VolumeCapPercent;
        _master.Ceiling = cap;
        _master.Mark = cap < 100 ? cap : null;
        var volume = _app.Controller.Volume ?? _app.Config.Volume;
        ShowVolume(volume ?? 0);
        if (volume is null) _masterValue.Text = "--";
        if (_app.NativeMixerView) { _masterTitle.Text = L.T("本机"); _masterValue.Text = "—"; }
    }

    /// <summary>Move the master slider without sending anything.</summary>
    public void ShowVolume(double percent)
    {
        if (_master.IsDragging || _masterWait.Enabled) return;
        _master.Value = (int)Math.Round(percent);
        _masterValue.Text = _master.Value.ToString();
    }

    public void UpdateState()
    {
        if (_app.Controller.State == StreamState.Streaming && _app.Controller.Volume is { } v) ShowVolume(v);
        if (_active) SyncRoutes();
    }

    public void ShowSoundOptions() => ShowMaster();

    /// <summary>Save the old speaker's pending slider choice (and apps' HomePod levels) before switching a complete profile.</summary>
    public void FlushPendingVolume()
    {
        foreach (var row in _byKey.Values) row.FlushSend();
        if (!_masterWait.Enabled) return;
        _masterWait.Stop();
        _app.SetVolume(_master.Value);
        VolumeApplied?.Invoke(_master.Value);
    }

    /// <summary>
    /// The HomePod level an app's independent volume starts at when it is turned on: the level it had when it was
    /// last turned off, but never above the app's Windows level now (rounded down to whole percent), so turning it
    /// on never makes the HomePod louder; without a remembered level exactly what the HomePod already plays. The
    /// user raises it with the slider.
    /// </summary>
    internal static int SeedSendPercent(int? remembered, float windowsLevel)
    {
        int windows = float.IsFinite(windowsLevel) ? (int)Math.Floor(Math.Clamp(windowsLevel, 0f, 1f) * 100 + 0.001) : 0;
        return remembered is { } r ? Math.Min(Math.Clamp(r, 0, 100), windows) : windows;
    }

    public void CancelPendingVolume() => _masterWait.Stop();

    // ---------------------------------------------------------------- lifetime

    public override void PageShown()
    {
        _active = true;
        ShowMaster();
        LoadCaptureDevices();
        RefreshApps();
        SyncRoutes();
        _meterTimer.Start();
        _refreshTimer.Start();
    }

    public override void PageHidden()
    {
        _active = false;
        _meterTimer.Stop();
        _refreshTimer.Stop();
        ReleaseSessions();
    }

    /// <summary>
    /// Leaving the page lets go of the apps' audio sessions but keeps their rows: shown again with the same apps, the rows
    /// take the fresh sessions (<see cref="RefreshApps"/>) instead of being built again. 2026-10-15 on the maintainer's PC:
    /// a window handle takes about 1.4 ms to create, and five apps' rows have 70 of them, 100 of the 160 ms that switching
    /// to 混音器 took.
    /// </summary>
    private void ReleaseSessions()
    {
        foreach (var row in _byKey.Values)
        {
            row.FlushSend();
            row.ResetMeter();
        }
        foreach (var a in _apps) a.Dispose();
        _apps = [];
    }

    /// <summary>The apps the rows show, in their order.</summary>
    private IEnumerable<string> RowKeys() => _rows.Controls.OfType<AppRow>().Select(r => r.Key);

    private void ClearRows()
    {
        _rows.SuspendLayout();
        foreach (var row in _byKey.Values) row.Dispose();
        _rows.Controls.Clear();
        _byKey.Clear();
        _rows.ResumeLayout(false);
        foreach (var a in _apps) a.Dispose();
        _apps = [];
    }

    // ---------------------------------------------------------------- app rows

    /// <summary>The apps playing on a device (null: the default output); tests give made-up ones.</summary>
    internal Func<string?, List<AppAudio>> FindApps { get; set; } = AppAudio.Enumerate;
    internal Func<List<AudioEndpoint>> FindCaptureOutputs { get; set; } = AudioEndpoints.Outputs;
    internal Func<bool> IsLocalOutputSilent { get; set; } = AppRouting.LocalOutputSilent;
    internal Func<bool> NativeOutputAvailable { get; set; } = () => AudioEndpoints.DefaultOutputId() != null;

    private void RefreshApps()
    {
        var fresh = FindApps(_app.Routing.CaptureDeviceId); // the apps on the captured device
#if DEBUG
        if (DemoApps)
        {
            foreach (var real in fresh) real.Dispose();
            fresh = DemoAppList();
        }
#endif
        // Not our own row (sync-test click, mic monitoring): muting or routing it would only break the sync test.
        foreach (var own in fresh.Where(IsOwn)) own.Dispose();
        fresh.RemoveAll(IsOwn);
        _empty.Collapsed = fresh.Count > 0; // also when nothing changed: the first refresh may find no apps at all
        if (fresh.Select(a => a.Key).SequenceEqual(RowKeys()))
        {
            if (_apps.Count == 0)
            {
                // Shown again with the apps it was left with (ReleaseSessions): the same rows, on the fresh sessions.
                foreach (var app in fresh) _byKey[app.Key].Bind(app);
                _apps = fresh;
            }
            else
            {
                foreach (var a in fresh) a.Dispose();
            }
            SyncValues();
            return;
        }
        // The page lays the new rows out once, at their width (each Add laid the list out, and the new row at a default 200 px).
        SuspendLayout();
        _rows.SuspendLayout();
        ClearRows();
        _apps = fresh;
        foreach (var app in fresh)
        {
            var row = new AppRow(app, this);
            _byKey[app.Key] = row;
            row.SuspendLayout();
            _rows.Controls.Add(row);
            row.ResumeLayout(false);
        }
        _rows.ResumeLayout(false);
        ResumeLayout(false);
        LayoutPanel.Relayout(_rows);
    }

    private static bool IsOwn(AppAudio app) => !app.IsSystemSounds && app.ProcessId == (uint)Environment.ProcessId;

#if DEBUG
    private static readonly bool DemoApps = Environment.GetEnvironmentVariable("HOMEPODCAST_DEMO_APPS") is { Length: > 0 };

    /// <summary>
    /// Debug builds with HOMEPODCAST_DEMO_APPS set (tutorial and promo recordings): made-up apps instead of the PC's own,
    /// so a recording shows no real program and no real session is ever touched. These rows have no sessions, so their
    /// volume and mute are no-ops, and routes set on them only go into that profile's config. Release builds have no
    /// such path.
    /// </summary>
    private static List<AppAudio> DemoAppList()
    {
        bool zh = L.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        var start = Environment.TickCount64;
        float Meter(double phase, double level) =>
            (float)(level * (0.55 + 0.45 * Math.Abs(Math.Sin((Environment.TickCount64 - start) / 180.0 + phase))));
        return
        [
            new(900001, false, zh ? "游戏" : "Game", DemoIcon(Color.FromArgb(214, 69, 69), "G"))
                { ExeKey = "demo-game.exe", FakePeak = () => Meter(0, 0.8) },
            new(900002, false, zh ? "语音聊天" : "Voice chat", DemoIcon(Color.FromArgb(88, 101, 242), "V"))
                { ExeKey = "demo-voice.exe", FakePeak = () => Meter(1.7, 0.45) },
            new(900003, false, zh ? "浏览器" : "Browser", DemoIcon(Color.FromArgb(0, 120, 212), "B"))
                { ExeKey = "demo-browser.exe", FakePeak = () => Meter(3.1, 0.3) },
        ];
    }

    private static readonly Dictionary<string, Icon> DemoIcons = [];

    /// <summary>A copy the row owns (AppAudio disposes its icon on every refresh); the drawn original is kept.</summary>
    private static Icon DemoIcon(Color color, string letter)
    {
        if (!DemoIcons.TryGetValue(letter, out var icon)) DemoIcons[letter] = icon = DrawDemoIcon(color, letter);
        return (Icon)icon.Clone();
    }

    private static Icon DrawDemoIcon(Color color, string letter)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var fill = new SolidBrush(color);
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(0, 0, 12, 12, 180, 90);
            path.AddArc(19, 0, 12, 12, 270, 90);
            path.AddArc(19, 19, 12, 12, 0, 90);
            path.AddArc(0, 19, 12, 12, 90, 90);
            path.CloseFigure();
            g.FillPath(fill, path);
            using var font = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel);
            using var white = new SolidBrush(Color.White);
            var size = g.MeasureString(letter, font);
            g.DrawString(letter, font, white, (32 - size.Width) / 2, (32 - size.Height) / 2);
        }
        return Icon.FromHandle(bmp.GetHicon()); // three per run, kept for the run
    }
#endif

    /// <summary>Pick up changes made elsewhere (e.g. in the Windows mixer) without fighting the user.</summary>
    private void SyncValues()
    {
        foreach (var row in _byKey.Values) row.Sync();
    }

    private void UpdateMeters()
    {
        foreach (var row in _byKey.Values) row.UpdateMeter();
    }

    /// <summary>Show the current rules (also for other rows of the same program) and the routing status.</summary>
    private void SyncRoutes()
    {
        var routing = _app.Routing;
        SyncDestinationChoices();
        foreach (var row in _byKey.Values) row.SyncRoute(routing);
        _routeRow.DescriptionText.Text = RouteStatus();
        _routeRow.DescriptionText.Role = !AppRouting.Supported ? TextRole.Caution : TextRole.Secondary;
    }

    /// <summary>
    /// The first time a change would switch per-app routing on, say what it costs (about 35 ms more latency
    /// on everything sent to the speaker) and let the user back out. Asked once; a yes is remembered.
    /// </summary>
    private bool ConfirmRouting(RouteRules next)
    {
        if (_app.Config.RoutingNoticeAccepted || _app.Routing.Active || !AppRouting.Supported || !next.NeedsRouting)
            return true;
        var answer = MessageBox.Show(FindForm(),
            L.F("按程序采集会让送往音箱的声音多约 {0} ms 延迟，游戏无法补偿。要继续吗？", RoutedCapture.RoutedExtraLatencyMs),
            L.T("按程序分流会增加延迟"), MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return false;
        _app.Config.RoutingNoticeAccepted = true;
        _app.Config.Save();
        return true;
    }

    private string RouteStatus()
    {
        var routing = _app.Routing;
        if (!AppRouting.Supported)
            return L.T("按程序分流需要 Windows 10 2004 或更高版本，现在所有声音都推送到音箱。");
        var settings = MixerRoutes.Read(_app.Config).Settings;
        if (settings != null && new[] { settings.Default }.Concat(settings.Apps.Values).All(r => r.Output.Kind == MixerOutputKind.Local))
            return L.T("本机播放由 Windows 决定；不创建播放副本，也不提供网络延迟设置。");
        if (!routing.Active)
            return L.T("所有程序送到同一个远端去处，延迟最低；本机是否发声由 Windows 决定。");
        var lines = new List<string>
        {
            L.F("按程序采集额外延迟约 {0} ms。", RoutedCapture.RoutedExtraLatencyMs),
        };
        if (_app.Controller.State == StreamState.Idle) lines.Add(L.T("连接音箱后生效。"));
        bool localWanted = routing.Default != AudioRoute.HomePod || routing.Rules.Apps.Values.Any(r => r != AudioRoute.HomePod);
        if (localWanted && IsLocalOutputSilent())
            lines.Add(L.T("Windows 输出已静音：本机没有声音。"));
        return string.Join("\n", lines);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _meterTimer.Dispose();
            _refreshTimer.Dispose();
            _masterWait.Dispose();
            ClearRows(); // rows first: they let go of their tooltips
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// One app: icon, name with its level meter and destination, then labeled Windows and HomePod controls.
    /// Narrow rows place destination on its own line. Independent send limits appear below its controls.
    /// </summary>
    private sealed class AppRow : Card
    {
        private AppAudio _app; // the sessions of one process (Key); a fresh enumeration's after the page was left (Bind)
        private readonly MixerPage _page;
        private readonly AppIconBox _icon;
        private readonly TextBlock _name;
        private readonly LevelMeter _meter = new() { Thickness = 3 };
        private readonly Control _route;
        private readonly FluentSlider _slider = Ui.Slider(0, 100, 1, 5);
        private readonly TextBlock _value = new("", TextStyle.Body, TextRole.Secondary) { Align = HorizontalAlignment.Right };
        private readonly TextBlock _localLabel = new(L.T("Windows 音量"), TextStyle.Caption, TextRole.Secondary);
        private readonly TextBlock _sendLabel = new(L.T("音箱音量"), TextStyle.Caption, TextRole.Secondary);
        private readonly ToggleSwitch _independent = new();
        private readonly FluentSlider _send = Ui.Slider(0, 100, 1, 5);
        private readonly TextBlock _sendValue = new("", TextStyle.Body, TextRole.Secondary) { Align = HorizontalAlignment.Right };
        private readonly TextBlock _sendStatus = new("", TextStyle.Caption, TextRole.Caution, wrap: true);
        private readonly TimerDebounce _sendWait = new(150); // the HomePod level is live at once; saved when the slider rests
        private readonly FluentButton _mute;

        public AppRow(AppAudio app, MixerPage page)
        {
            _app = app;
            Key = app.Key;
            _page = page;
            Padding = new Padding(12, 8, 12, 8);
            _icon = new AppIconBox(app.Icon);
            _name = new TextBlock(app.Name);
            _mute = new FluentButton("", ButtonKind.Subtle, Glyph.Volume) { AccessibleName = L.F("{0} 静音", app.Name) };
            _slider.AccessibleName = L.F("{0} Windows 音量", app.Name);
            _slider.PreferredLength = 150;
            _send.AccessibleName = L.F("{0} 音箱音量", app.Name);
            _independent.AccessibleName = L.F("{0} 独立音量", app.Name);
            _send.PreferredLength = 150;
            _independent.Enabled = AppRouting.Supported && !app.IsSystemSounds && app.ExeKey.Length > 0;
            _route = RouteCell();
            Controls.AddRange([_icon, _name, _meter, _route, _localLabel, _slider, _value, _mute,
                _sendLabel, _independent, _send, _sendValue, _sendStatus]);
            _page._tips.SetToolTip(_independent, L.T("独立控制此程序送往音箱的音量"));
            _page._tips.SetToolTip(_send, L.T("Windows 静音或音量为 0 时，音箱也没有声音。几个声音会话的 Windows 音量不一致或低于 1% 时，音箱暂为 Windows 音量 × 这里的音量。"));

            try { _slider.Value = (int)Math.Round(app.Volume * 100); } catch { }
            _value.Text = _slider.Value.ToString();
            ShowMute(SafeMuted());
            var routing = page._app.Routing;
            _send.Value = routing.GetSendVolume(app.ExeKey) ?? SeedSendPercent(routing.LastSendVolume(app.ExeKey), _slider.Value / 100f);

            _slider.Scroll += (_, _) =>
            {
                _value.Text = _slider.Value.ToString();
                try { _app.Volume = _slider.Value / 100f; } catch (Exception ex) { Log.Warn($"mixer: {ex.Message}"); }
            };
            _mute.Click += (_, _) =>
            {
                bool muted = !SafeMuted();
                try { _app.Muted = muted; } catch (Exception ex) { Log.Warn($"mixer: {ex.Message}"); }
                ShowMute(SafeMuted());
            };
            _independent.Toggled += (_, _) =>
            {
                var routing = _page._app.Routing;
                string key = RouteRules.KeyFor(_app.ExeKey);
                // From what the HomePod plays now: never above the app's Windows level, so nothing gets louder.
                int? level = _independent.Checked ? SeedSendPercent(routing.LastSendVolume(key), WindowsLevel()) : null;
                var sends = new Dictionary<string, int>(routing.Rules.SendVolumes);
                if (level is { } l) sends[key] = l;
                else sends.Remove(key);
                if (!_page.ConfirmRouting(routing.Rules with { SendVolumes = sends }))
                {
                    _page.SyncRoutes();
                    return;
                }
                _sendWait.Stop(); // turning it on or off saves (and remembers) the level anyway
                routing.SetSendVolume(key, level);
                _page.SyncRoutes();
            };
            _send.Scroll += (_, _) =>
            {
                if (!_independent.Checked) return;
                _page._app.Routing.PreviewSendVolume(_app.ExeKey, _send.Value); // the capture follows the drag
                _sendValue.Text = _send.Value.ToString();
                _sendWait.Restart();
            };
            _sendWait.Tick += FlushSend;
        }

        /// <summary>The process (AppAudio.Key): its name, icon and destination stay this row's.</summary>
        public string Key { get; }

        /// <summary>The same process's sessions from a fresh enumeration (the old ones were let go when the page was left).</summary>
        public void Bind(AppAudio app) => _app = app;

        public void ResetMeter() => _meter.Reset();

        /// <summary>Save (and log) a HomePod level the slider set, if it has not been yet.</summary>
        public void FlushSend()
        {
            if (!_sendWait.Enabled) return;
            _sendWait.Stop();
            _page._app.Routing.CommitSendVolume(_app.ExeKey);
        }

        /// <summary>The app's Windows level now (the last one shown if it cannot be read).</summary>
        private float WindowsLevel()
        {
            try { return _app.Volume; } catch { return _slider.Value / 100f; }
        }

        private bool SafeMuted()
        {
            try { return _app.Muted; } catch { return false; }
        }

        private void ShowMute(bool muted)
        {
            _mute.Glyph = muted ? Glyph.Mute : Glyph.Volume;
            _mute.GlyphRole = muted ? TextRole.Critical : null;
            _page._tips.SetToolTip(_mute, muted ? L.T("取消静音") : L.T("静音"));
            _mute.Invalidate();
        }

        /// <summary>Destination: segmented control, or a note for system sounds / unknown programs.</summary>
        private Control RouteCell()
        {
            if (_app.IsSystemSounds || _app.ExeKey.Length == 0)
            {
                var label = new TextBlock("—", TextStyle.Caption, TextRole.Secondary, nativeWindow: true);
                _page._tips.SetToolTip(label, _app.IsSystemSounds
                    ? L.T("系统声音不属于某一个程序，不能单独设置：按程序分流时只在本机播放。")
                    : L.T("读不到这个程序的文件名，不能单独设置。"));
                return label;
            }
            var combo = new FluentComboBox
            {
                MinWidth = 220, PreferredWidth = 290,
                Enabled = AppRouting.Supported,
                AccessibleName = L.F("{0} 输出到", _app.Name),
            };
            combo.SelectionChangeCommitted += async (_, _) => await _page.ApplyAppChoice(_app.ExeKey, combo);
            return combo;
        }

        public void SyncRoute(AppRouting routing)
        {
            string key = RouteRules.KeyFor(_app.ExeKey);
            int? level = routing.GetSendVolume(key);
            bool independent = level is { };
            bool sent = routing.Rules.For(key) != AudioRoute.Local;
            _independent.Checked = independent;
            _send.Enabled = independent && _independent.Enabled && sent;
            // Off: where turning it on would start (see SeedSendPercent).
            if (!_send.IsDragging && !_sendWait.Enabled)
                _send.Value = level ?? SeedSendPercent(routing.LastSendVolume(key), _slider.Value / 100f);
            _sendValue.Text = independent ? _send.Value.ToString() : "—";
            _sendStatus.Text = independent && sent && routing.Active && _page._app.Controller.State == StreamState.Streaming
                ? routing.SendStatus(key) switch
                {
                    SendVolumeStatus.MutedOrZero => L.T("Windows 已静音或音量为 0，音箱没有声音。"),
                    SendVolumeStatus.DifferentSessionLevels => L.T("此程序几个声音会话的 Windows 音量不一致：音箱暂为 Windows 音量 × HomePod 音量，比设定的小。"),
                    SendVolumeStatus.BelowMinimum => L.T("Windows 音量低于 1%：音箱暂为 Windows 音量 × HomePod 音量，几乎听不到。"),
                    _ => "",
                } : "";
            if (_route is FluentComboBox combo)
            {
                _page.SyncAppChoice(_app.ExeKey, combo);
                string? owner = _page._app.Mixer?.OwnerKeyForProcess(_app.ProcessId);
                bool nested = owner != null && owner != RouteRules.KeyFor(_app.ExeKey);
                if (nested) { combo.Enabled = false; _independent.Enabled = false; }
                _page._tips.SetToolTip(combo, nested ? L.F("此程序随父程序「{0}」的去处播放，不能单独分开采集。", owner!) : combo.SelectedText ?? _page.DefaultRouteText());
            }
            else if (_route is TextBlock label)
            {
                label.Text = _app.IsSystemSounds && routing.Active ? L.T("本机") : "—";
            }
        }

        public void Sync()
        {
            try
            {
                if (!_slider.IsDragging)
                {
                    int v = (int)Math.Round(_app.Volume * 100);
                    if (v != _slider.Value)
                    {
                        _slider.Value = v;
                        _value.Text = v.ToString();
                    }
                }
                ShowMute(_app.Muted);
            }
            catch { }
        }

        public void UpdateMeter()
        {
            try { _meter.SetLevel(_app.Muted ? 0 : _app.Peak); } catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                FlushSend(); // the rows go when the page is left: a level still waiting is saved now
                if (_route is FluentComboBox combo) _page._destinationChoices.Remove(combo);
                _sendWait.Dispose();
                // The page's tooltip would otherwise keep every row ever shown.
                _page._tips.SetToolTip(_mute, null);
                _page._tips.SetToolTip(_route, null);
                _page._tips.SetToolTip(_independent, null);
                _page._tips.SetToolTip(_send, null);
            }
            base.Dispose(disposing);
        }

        protected override int Arrange(int width, bool apply)
        {
            int padX = Dp(Padding.Left), padY = Dp(Padding.Top);
            int inner = width - padX * 2;
            int icon = Dp(24), gap = Dp(12), small = Dp(6);
            // Same column for every row (system sounds show a note there), so the sliders line up.
            int routeW = Math.Max(WidthFor(_route), Dp(240));
            int valueW = Dp(34), muteW = Dp(32);
            int nameH = HeightFor(_name, 100), meterH = Dp(6);
            int nameBlock = nameH + Dp(4) + meterH;
            int rest = gap + routeW + small + muteW;
            int nameW = inner - icon - gap - rest;
            int rowH = Math.Max(Dp(36), nameBlock);

            bool routeOnHeader = nameW >= Dp(130);
            if (apply)
            {
                int cy = padY + rowH / 2, textX = padX + icon + gap;
                int textW = routeOnHeader ? nameW : inner - icon - gap - small - muteW;
                Place(_icon, padX, cy - icon / 2, icon, icon);
                Place(_name, textX, cy - nameBlock / 2, textW, nameH);
                Place(_meter, textX, cy - nameBlock / 2 + nameH + Dp(4), Math.Min(textW, Dp(160)), meterH);
                Place(_mute, padX + inner - muteW, cy - muteW / 2, muteW, muteW);
                if (routeOnHeader)
                    Place(_route, padX + inner - muteW - small - routeW, cy - HeightFor(_route, routeW) / 2,
                        routeW, HeightFor(_route, routeW));
            }
            int y = padY + rowH + small;
            if (!routeOnHeader)
            {
                int rw = Math.Min(routeW, inner), rh = HeightFor(_route, rw);
                if (apply) Place(_route, padX, y, rw, rh);
                y += rh + small;
            }
            int line = Dp(32);
            int labelW = Math.Min(Math.Max(Dp(120), Math.Max(WidthFor(_localLabel), WidthFor(_sendLabel))),
                Math.Max(Dp(80), inner / 3));
            int toggleW = Dp(44);
            int sliderX = padX + labelW + small;
            int valueX = padX + inner - valueW;
            if (apply)
            {
                Place(_localLabel, padX, y + (line - HeightFor(_localLabel, labelW)) / 2, labelW, HeightFor(_localLabel, labelW));
                Place(_slider, sliderX, y, Math.Max(Dp(40), valueX - small - sliderX), line);
                Place(_value, valueX, y + (line - HeightFor(_value, valueW)) / 2, valueW, HeightFor(_value, valueW));
                int sendY = y + line + small, sendX = sliderX + toggleW + small;
                Place(_sendLabel, padX, sendY + (line - HeightFor(_sendLabel, labelW)) / 2, labelW, HeightFor(_sendLabel, labelW));
                Place(_independent, sliderX, sendY + (line - Dp(24)) / 2, toggleW, Dp(24));
                Place(_send, sendX, sendY, Math.Max(Dp(40), valueX - small - sendX), line);
                Place(_sendValue, valueX, sendY + (line - HeightFor(_sendValue, valueW)) / 2, valueW, HeightFor(_sendValue, valueW));
            }
            int end = y + line * 2 + small;
            int statusH = HeightFor(_sendStatus, inner);
            if (apply) Place(_sendStatus, padX, end, inner, statusH);
            return end + statusH + padY;
        }
    }
}
