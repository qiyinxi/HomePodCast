using HomePodCast.Audio;
using HomePodCast.UI.Controls;

namespace HomePodCast.UI.Pages;

/// <summary>
/// 混音器: the speaker's volume, which output device is captured (采集设备: the Windows default output, or a
/// device of the user's choice for routing by sound card), where apps without their own rule go (HomePod /
/// 本机 / 两者) and what routing does now, and one bordered row per app on the captured device with a live
/// level meter, its destination, volume and mute. App rows are created while the page is visible; everything
/// is sized for the page's DPI.
/// </summary>
internal sealed class MixerPage : ScrollPage
{
    private static readonly AudioRoute[] Routes = [AudioRoute.HomePod, AudioRoute.Local, AudioRoute.Both];

    /// <summary>Windows Settings → 应用音量和设备首选项, where each app's output device is chosen.</summary>
    private const string AppVolumeSettings = "ms-settings:apps-volume";

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

    private readonly Segmented _routeDefault = new(Routes.Select(RouteName).ToArray()) { EqualWidths = false };
    private readonly SettingRow _routeRow;

    private readonly StackPanel _rows = new() { Gap = 4 };
    private readonly TextBlock _empty = new(L.T("现在没有程序在发声。"), TextStyle.Body, TextRole.Secondary);
    private readonly Dictionary<string, AppRow> _byKey = new();
    private List<AppAudio> _apps = [];
    private bool _active;

    public MixerPage(TrayApp app) : base(L.T("混音器"))
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
        _routeRow = new SettingRow(Glyph.Devices, L.T("没单独设置的程序送到"), "", _routeDefault);
        _routeRow.DescriptionText.Wrap = true;

        var appVolume = new FluentButton(L.T("打开 Windows 应用音量设置"), ButtonKind.Link, Glyph.OpenInNew);
        var modes = Ui.Card(
            Ui.Header(L.T("全部推送与按程序分流")),
            Ui.Stack(4,
                new TextBlock(L.T("全部推送（默认，推荐打游戏）"), TextStyle.BodyStrong),
                Ui.Note(L.T("直接抓取整个声音输出，没有额外延迟。所有程序都送到 HomePod；本机出不出声由 Windows 音量决定，把 Windows 设为静音就只在音箱播放。"))),
            Ui.Stack(4,
                new TextBlock(L.F("按程序分流（额外延迟约 {0} ms）", RoutedCapture.RoutedExtraLatencyMs), TextStyle.BodyStrong),
                Ui.Note(L.F("只要有程序单独设了去处，或默认去处是「本机」，就会启用。Windows 只能逐个抓取程序的声音，这条路更慢：推到 HomePod 的声音会多约 {0} ms。浏览器插件会把这部分自动算进画面延迟，游戏没法补偿。设为「HomePod」的程序在本机静音，Windows 音量合成器里显示为 0%；系统提示音只在本机播放。全部改回「默认」并把默认设为 HomePod，就回到全部推送。", RoutedCapture.RoutedExtraLatencyMs))),
            Ui.Stack(4,
                new TextBlock(L.T("按声卡分流（没有额外延迟）"), TextStyle.BodyStrong),
                Ui.Note(L.F("在 Windows 的应用音量设置（或程序自己的设置）里，把要推送的程序的输出设为一个你不用来听的设备，再在上面的「采集设备」里选它；语音聊天等其他程序照常在耳机里播放。整个设备一次抓取，不像按程序分流那样多约 {0} ms。需要一个空闲的或虚拟的声卡，比如 VB-CABLE、网易 UU 加速器的虚拟声卡、Steam Streaming Speakers，或不带喇叭的 HDMI 显示器；HomePodCast 没法自己装虚拟声卡（那是要微软签名的内核驱动）。有些游戏只在启动时选择输出设备，改完要重启游戏。", RoutedCapture.RoutedExtraLatencyMs)),
                Ui.Row(0, null, appVolume)));

        var appsHeader = Ui.Section(L.T("应用（推送到音箱的声音）"));
        var note = Ui.Note(L.T("和 Windows 音量合成器是同一套设置，系统会记住每个程序的音量。"));
        Content.Controls.AddRange([masterCard, _captureRow, _routeRow, modes, appsHeader, _rows, _empty, note]);
        Content.GapBefore[appsHeader] = 20;
        Content.GapBefore[_rows] = 8;
        _empty.Collapsed = true;

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
        _routeDefault.SelectionChangeCommitted += (_, _) =>
        {
            if (_routeDefault.SelectedIndex < 0) return;
            var route = Routes[_routeDefault.SelectedIndex];
            if (!ConfirmRouting(_app.Routing.Rules with { Default = route }))
            {
                SyncRoutes();
                return;
            }
            _app.Routing.Default = route;
            SyncRoutes();
        };
        _captureDevice.SelectionChangeCommitted += (_, _) => OnCaptureDeviceChosen();
        appVolume.Click += (_, _) => OpenAppVolumeSettings();
        _meterTimer.Tick += (_, _) => UpdateMeters();
        _refreshTimer.Tick += (_, _) =>
        {
            RefreshApps();
            SyncRoutes();
        };
        ShowMaster();
    }

    // ---------------------------------------------------------------- capture device

    /// <summary>
    /// 跟随 Windows 默认输出, then every active output; a chosen device that is gone stays listed as unavailable
    /// (it is still what gets captured once it is back; nothing falls back silently).
    /// </summary>
    private void LoadCaptureDevices()
    {
        if (_captureDevice.DroppedDown) return; // the next device change or page visit refreshes it
        _captureOutputs = AudioEndpoints.Outputs();
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
              (_app.Config.VolumeKeys == VolumeKeyMode.FollowWindows ? "\n" + L.T(CaptureVolumeNote) : "");
        desc.Role = missing ? TextRole.Caution : TextRole.Secondary;
    }

    /// <summary>An output device came, went, or changed state (TrayApp, from the device notifications).</summary>
    public void AudioDevicesChanged()
    {
        if (!_active) return; // PageShown lists them afresh
        LoadCaptureDevices();
        RefreshApps();
    }

    private void OpenAppVolumeSettings()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppVolumeSettings) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"open {AppVolumeSettings}: {ex.Message}"); }
    }

    /// <summary>The volume was changed here (首页 follows).</summary>
    public event Action<double>? VolumeApplied;

    private static string RouteName(AudioRoute route) => route switch
    {
        AudioRoute.Local => L.T("本机"),
        AudioRoute.Both => L.T("两者"),
        _ => "HomePod",
    };

    /// <summary>Destination choices of an app row.</summary>
    private static string[] RouteItems => [L.T("默认"), .. Routes.Select(RouteName)];

    private string DefaultRouteText() => L.F("默认（{0}）", RouteName(_app.Routing.Default));

    // ---------------------------------------------------------------- speaker volume

    private void ShowMaster()
    {
        _masterTitle.Text = L.F("{0} 音量", _app.Config.DeviceName ?? "HomePod");
        int cap = _app.Config.VolumeCapPercent;
        _master.Ceiling = cap;
        _master.Mark = cap < 100 ? cap : null;
        var volume = _app.Controller.Volume ?? _app.Config.Volume;
        ShowVolume(volume ?? 0);
        if (volume is null) _masterValue.Text = "--";
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
        ClearRows();
    }

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

    private void RefreshApps()
    {
        var fresh = AppAudio.Enumerate(_app.Routing.CaptureDeviceId); // the apps on the captured device
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
        if (fresh.Select(a => a.Key).SequenceEqual(_apps.Select(a => a.Key)))
        {
            foreach (var a in fresh) a.Dispose();
            SyncValues();
            return;
        }
        SuspendLayout();
        ClearRows();
        _apps = fresh;
        foreach (var app in fresh)
        {
            var row = new AppRow(app, this);
            _byKey[app.Key] = row;
            _rows.Controls.Add(row);
        }
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
        _routeDefault.SelectedIndex = Array.IndexOf(Routes, routing.Default);
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
            L.F("给程序单独设置去处后会改用「按程序分流」：Windows 只能逐个抓取程序的声音，推到 HomePod 的声音会比「全部推送」多约 {0} ms 延迟（浏览器插件会自动补偿画面，游戏无法补偿）。\n\n设为「HomePod」的程序会在本机静音（Windows 音量合成器里显示 0%），系统提示音只在本机播放。\n\n打游戏最在意延迟时，建议保持全部推送。要启用按程序分流吗？", RoutedCapture.RoutedExtraLatencyMs),
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
        if (!routing.Active)
            return L.T("所有程序都推送到音箱，延迟最低；本机出不出声由 Windows 音量决定。");
        var lines = new List<string>
        {
            L.F("已按程序分流：推送到音箱的声音多约 {0} ms 延迟，设为「HomePod」的程序在本机静音。", RoutedCapture.RoutedExtraLatencyMs),
        };
        if (_app.Controller.State == StreamState.Idle) lines.Add(L.T("连接音箱后生效。"));
        bool localWanted = routing.Default != AudioRoute.HomePod || routing.Rules.Apps.Values.Any(r => r != AudioRoute.HomePod);
        if (localWanted && AppRouting.LocalOutputSilent())
            lines.Add(L.T("Windows 输出已静音：设为「本机」或「两者」的程序在这台电脑上也听不到。"));
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
    /// One app: icon, name with its level meter, destination, volume and mute. One line when there is room,
    /// otherwise destination and volume move to a second line.
    /// </summary>
    private sealed class AppRow : Card
    {
        private readonly AppAudio _app;
        private readonly MixerPage _page;
        private readonly AppIconBox _icon;
        private readonly TextBlock _name;
        private readonly LevelMeter _meter = new() { Thickness = 3 };
        private readonly Control _route;
        private readonly FluentSlider _slider = Ui.Slider(0, 100, 1, 5);
        private readonly TextBlock _value = new("", TextStyle.Body, TextRole.Secondary) { Align = HorizontalAlignment.Right };
        private readonly FluentButton _mute;

        public AppRow(AppAudio app, MixerPage page)
        {
            _app = app;
            _page = page;
            Padding = new Padding(12, 8, 12, 8);
            _icon = new AppIconBox(app.Icon);
            _name = new TextBlock(app.Name);
            _mute = new FluentButton("", ButtonKind.Subtle, Glyph.Volume) { AccessibleName = L.F("{0} 静音", app.Name) };
            _slider.AccessibleName = L.F("{0} 音量", app.Name);
            _slider.PreferredLength = 150;
            _route = RouteCell();
            Controls.AddRange([_icon, _name, _meter, _route, _slider, _value, _mute]);

            try { _slider.Value = (int)Math.Round(app.Volume * 100); } catch { }
            _value.Text = _slider.Value.ToString();
            ShowMute(SafeMuted());

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
        }

        private bool SafeMuted()
        {
            try { return _app.Muted; } catch { return false; }
        }

        private void ShowMute(bool muted)
        {
            _mute.Glyph = muted ? Glyph.Mute : Glyph.Volume;
            _mute.GlyphColor = muted ? Theme.P.Critical : null;
            _page._tips.SetToolTip(_mute, muted ? L.T("取消静音") : L.T("静音"));
            _mute.Invalidate();
        }

        /// <summary>Destination: segmented control, or a note for system sounds / unknown programs.</summary>
        private Control RouteCell()
        {
            if (_app.IsSystemSounds || _app.ExeKey.Length == 0)
            {
                var label = new TextBlock("—", TextStyle.Caption, TextRole.Secondary);
                _page._tips.SetToolTip(label, _app.IsSystemSounds
                    ? L.T("系统声音不属于某一个程序，不能单独设置：按程序分流时只在本机播放。")
                    : L.T("读不到这个程序的文件名，不能单独设置。"));
                return label;
            }
            var seg = new Segmented(RouteItems)
            {
                Compact = true,
                EqualWidths = false,
                Enabled = AppRouting.Supported,
                AccessibleName = L.F("{0} 送到", _app.Name),
            };
            seg.SelectionChangeCommitted += (_, _) =>
            {
                if (seg.SelectedIndex < 0) return;
                AudioRoute? route = seg.SelectedIndex == 0 ? null : Routes[seg.SelectedIndex - 1];
                var routing = _page._app.Routing;
                var apps = new Dictionary<string, AudioRoute>(routing.Rules.Apps);
                var key = RouteRules.KeyFor(_app.ExeKey);
                if (route is { } r) apps[key] = r;
                else apps.Remove(key);
                if (!_page.ConfirmRouting(routing.Rules with { Apps = apps }))
                {
                    _page.SyncRoutes();
                    return;
                }
                routing.Set(_app.ExeKey, route);
                _page.SyncRoutes();
            };
            return seg;
        }

        public void SyncRoute(AppRouting routing)
        {
            if (_route is Segmented seg)
            {
                seg.SelectedIndex = routing.Get(_app.ExeKey) is { } own ? Array.IndexOf(Routes, own) + 1 : 0;
                _page._tips.SetToolTip(seg, _page.DefaultRouteText() + "\n" +
                    L.T("HomePod：只在音箱播放，本机静音\n本机：只在这台电脑播放，不推送\n两者：音箱和本机都播放"));
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
                // The page's tooltip would otherwise keep every row ever shown.
                _page._tips.SetToolTip(_mute, null);
                _page._tips.SetToolTip(_route, null);
            }
            base.Dispose(disposing);
        }

        protected override int Arrange(int width, bool apply)
        {
            int padX = Dp(Padding.Left), padY = Dp(Padding.Top);
            int inner = width - padX * 2;
            int icon = Dp(24), gap = Dp(12), small = Dp(6);
            // Same column for every row (system sounds show a note there), so the sliders line up.
            int routeW = Math.Max(WidthFor(_route), Segmented.PreferredWidthFor(RouteItems, compact: true, equal: false, DeviceDpi));
            int sliderW = Dp(150), valueW = Dp(34), muteW = Dp(32);
            int nameH = HeightFor(_name, 100), meterH = Dp(6);
            int nameBlock = nameH + Dp(4) + meterH;
            int rest = gap + routeW + gap + sliderW + small + valueW + small + muteW;
            int nameW = inner - icon - gap - rest;
            int rowH = Math.Max(Dp(36), nameBlock);

            if (nameW >= Dp(130))
            {
                if (apply)
                {
                    int x = padX;
                    int cy = padY + rowH / 2;
                    Place(_icon, x, cy - icon / 2, icon, icon);
                    x += icon + gap;
                    Place(_name, x, cy - nameBlock / 2, nameW, nameH);
                    Place(_meter, x, cy - nameBlock / 2 + nameH + Dp(4), Math.Min(nameW, Dp(160)), meterH);
                    x += nameW + gap;
                    int rh = HeightFor(_route, routeW);
                    Place(_route, x, cy - rh / 2, routeW, rh);
                    x += routeW + gap;
                    Place(_slider, x, cy - Dp(16), sliderW, Dp(32));
                    x += sliderW + small;
                    int vh = HeightFor(_value, valueW);
                    Place(_value, x, cy - vh / 2, valueW, vh);
                    x += valueW + small;
                    Place(_mute, x, cy - muteW / 2, muteW, muteW);
                }
                return padY * 2 + rowH;
            }

            // Two lines: name and mute on top, destination and volume below.
            int line2 = Dp(34);
            if (apply)
            {
                int x = padX, cy = padY + rowH / 2;
                Place(_icon, x, cy - icon / 2, icon, icon);
                int textX = x + icon + gap;
                int textW = inner - icon - gap - small - muteW;
                Place(_name, textX, cy - nameBlock / 2, textW, nameH);
                Place(_meter, textX, cy - nameBlock / 2 + nameH + Dp(4), Math.Min(textW, Dp(160)), meterH);
                Place(_mute, padX + inner - muteW, cy - muteW / 2, muteW, muteW);

                int y2 = padY + rowH + Dp(6), cy2 = y2 + line2 / 2;
                int rw = Math.Min(routeW, inner - icon - gap);
                int rh = HeightFor(_route, rw);
                Place(_route, textX, cy2 - rh / 2, rw, rh);
                int sx = textX + rw + gap;
                int sw = Math.Max(Dp(60), padX + inner - valueW - small - sx);
                Place(_slider, sx, cy2 - Dp(16), sw, Dp(32));
                int vh = HeightFor(_value, valueW);
                Place(_value, padX + inner - valueW, cy2 - vh / 2, valueW, vh);
            }
            return padY * 2 + rowH + Dp(6) + line2;
        }
    }
}
