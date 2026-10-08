using HomePodCast.Audio;
using HomePodCast.UI.Controls;

namespace HomePodCast.UI.Pages;

/// <summary>
/// 混音器: the speaker's volume, where apps without their own rule go (HomePod / 本机 / 两者) and what
/// routing does now, and one bordered row per app with a live level meter, its destination, volume and
/// mute. App rows are created while the page is visible; everything is sized for the page's DPI.
/// </summary>
internal sealed class MixerPage : ScrollPage
{
    private static readonly AudioRoute[] Routes = [AudioRoute.HomePod, AudioRoute.Local, AudioRoute.Both];

    private readonly TrayApp _app;
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

        _routeDefault.Enabled = AppRouting.Supported;
        _routeRow = new SettingRow(Glyph.Devices, L.T("没单独设置的程序送到"), "", _routeDefault);
        _routeRow.DescriptionText.Wrap = true;

        var appsHeader = Ui.Section(L.T("应用（推送到音箱的声音）"));
        var note = Ui.Note(L.T("和 Windows 音量合成器是同一套设置，系统会记住每个程序的音量。"));
        Content.Controls.AddRange([masterCard, _routeRow, appsHeader, _rows, _empty, note]);
        Content.GapBefore[appsHeader] = 20;
        Content.GapBefore[_rows] = 8;
        _empty.Collapsed = true;

        _master.Scroll += (_, _) =>
        {
            _masterValue.Text = _master.Value.ToString();
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
            _app.Routing.Default = Routes[_routeDefault.SelectedIndex];
            SyncRoutes();
        };
        _meterTimer.Tick += (_, _) => UpdateMeters();
        _refreshTimer.Tick += (_, _) =>
        {
            RefreshApps();
            SyncRoutes();
        };
        ShowMaster();
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
        var fresh = AppAudio.Enumerate();
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
        _empty.Collapsed = fresh.Count > 0;
        ResumeLayout(false);
        LayoutPanel.Relayout(_rows);
    }

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
            _tips.Dispose();
            foreach (var a in _apps) a.Dispose();
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
                _page._app.Routing.Set(_app.ExeKey, seg.SelectedIndex == 0 ? null : Routes[seg.SelectedIndex - 1]);
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
