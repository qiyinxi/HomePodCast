using HomePodCast.Net;
using HomePodCast.UI.Controls;

namespace HomePodCast.UI.Pages;

/// <summary>
/// 首页: which speaker, what latency now, is anything wrong. Speaker card (picker, state, connect), scene
/// card (scene, latency, how far the sound lags the picture), volume card (volume, mute, night mode), and
/// tiles with the stream statistics the app actually measures, and the network's line (or a hint when Wi-Fi jitter
/// threatens the latency's margin).
/// </summary>
internal sealed class HomePage : ScrollPage
{
    private readonly TrayApp _app;
    private readonly ToolTip _tips = new();
    private readonly System.Windows.Forms.Timer _statsTimer = new() { Interval = 500 };

    // speaker
    // Wrapped: next to 连接 in half the window, 「还没有选择音箱」 / 「在下面选择一台 AirPlay 音箱」 lost their ends (German too).
    private readonly TextBlock _name = new("", TextStyle.Subtitle, wrap: true);
    private readonly TextBlock _model = new("", TextStyle.Caption, TextRole.Secondary, wrap: true);
    private readonly FluentButton _connect = new(L.T("连接"), ButtonKind.Primary) { MinWidth = 96 };
    private readonly FluentComboBox _device = new();
    private readonly FluentButton _refresh;
    private readonly StatusDot _dot = new();
    private readonly TextBlock _status = new("", TextStyle.Body, wrap: true);
    private readonly TextBlock _streamStats = Ui.Note();
    private List<AirPlayDevice> _devices = [];

    private StackPanel _homePodLatency = null!;
    private ReceiverLatencyPanel _receiverLatency = null!;
    private TextBlock _sceneHeader = null!;

    // scene / latency
    private readonly Segmented _scenes = new(Scenes.All.Select(Scenes.Name).ToArray()) { EqualWidths = false }; // "Recommended" is long
    private readonly TextBlock _latencyValue = new("", TextStyle.Display);
    private readonly TextBlock _latencyHint = new("", TextStyle.Caption, TextRole.Secondary, wrap: true); // "极限：…" is long
    private readonly TextBlock _lag = new("", TextStyle.Body, TextRole.Secondary, wrap: true);
    private readonly FluentSlider _latency = Ui.Slider(0, LatencyTuner.MaxMs, LatencyTuner.SmallStep, LatencyTuner.LargeStep);
    private readonly TextBlock _movieHint = Ui.Note();
    private int _lagMs;
    private readonly LatencyTuner _tuner;
    private readonly FluentComboBox _profiles = new() { MinWidth = 140, AccessibleName = L.T("使用配置"), Placeholder = L.T("选择配置…") };
    private readonly FluentButton _profileApply = new(L.T("应用"));
    private string[] _profileIds = [];
    private readonly TimerDebounce _sliderWait = new(1000), _sceneWait = new(1000);

    // volume
    private readonly TextBlock _volumeValue = new("", TextStyle.BodyStrong) { Align = HorizontalAlignment.Right };
    private readonly FluentSlider _volume = Ui.Slider(0, 100, 1, 5);
    private readonly ToggleSwitch _mute = new(L.T("静音"));
    private readonly ToggleSwitch _night = new(L.T("夜间模式"));
    private readonly TextBlock _capNote = Ui.Note();
    private readonly TimerDebounce _volumeWait = new(150);

    // statistics
    private readonly MetricTile _buffer = new(L.T("缓冲"), "ms");
    private readonly MetricTile _speakerDelay = new(L.T("音箱处理"), "ms");
    private readonly MetricTile _effective = new(L.T("实际延迟"), "ms");
    private readonly MetricTile _dropouts = new(L.T("断音"));
    private readonly MetricTile _resent = new(L.T("重传"));

    // network: a status line while streaming; instead of it, a hint when the jitter eats the latency's margin
    // (only the hint's button changes the latency)
    private readonly TextBlock _netStatus = new("", TextStyle.Body, TextRole.Secondary, wrap: true);
    private readonly RowPanel _netLine;
    private readonly TextBlock _netHintText = new("", TextStyle.Body, TextRole.Caution, wrap: true);
    private readonly FluentButton _netApply = new(L.T("应用"), ButtonKind.Secondary) { MinWidth = 72 };
    private readonly Card _netHint;
    private int? _suggestedMs;
    private int _appliedSession = -1; // Health.Session whose suggestion was applied: no hint again until it reconnects
    private bool _hintLogged;
    private string? _dropoutTip;

    // update: a banner above the cards when an automatic check found a new version (a manual one answers in 设置)
    private readonly TextBlock _updateText = new("", TextStyle.Body, TextRole.Accent, wrap: true);
    private readonly FluentButton _updateInstall = new(L.T("更新"), ButtonKind.Primary) { MinWidth = 72 };
    private readonly FluentButton _updateNotes = new(L.T("查看说明"), ButtonKind.Secondary, Glyph.OpenInNew);
    private readonly FluentButton _updateSkip = new(L.T("跳过此版本"), ButtonKind.Subtle);
    private readonly Card _updateBanner;

    // .NET was replaced under the running app: 「立即重启」 (RuntimeRestartNotice), above everything else
    private readonly RuntimeRestartNotice _runtimeNotice;

    private StreamState _lastState = StreamState.Idle;
    private DateTime _streamingSince;
    private long _underrunsAtStart;
    private bool _loading;

    public HomePage(TrayApp app) : base(L.T("首页"))
    {
        _app = app;
        _refresh = Ui.IconButton(Glyph.Refresh, L.T("刷新"), L.T("重新搜索音箱"), _tips);
        _tuner = new LatencyTuner(app.Config, app.SetLatency, _sliderWait, _sceneWait, app.Controller.SafeLatency(0))
        {
            IsHeld = () => _latency.IsDragging,
        };

        var left = Ui.Stack(12, BuildSpeakerCard(), BuildVolumeCard());
        var columns = new Columns(left, BuildSceneCard()) { MinColumnWidth = 330 };
        var tiles = new TileGrid { MinTileWidth = 112 };
        tiles.Controls.AddRange([_effective, _buffer, _speakerDelay, _dropouts, _resent]);
        // Diagnostics live in 设置 → 工具, marking a stutter on a hotkey (unset by default): neither is for every day.
        var statsHeader = Ui.Section(L.T("推流状态"));
        _netLine = Ui.Row(8, _netStatus, new GlyphLabel(Glyph.Wifi), _netStatus);
        _netLine.Collapsed = true;
        _netHint = BuildNetworkHint();
        _updateBanner = BuildUpdateBanner();
        _runtimeNotice = new RuntimeRestartNotice(_app.RestartForRuntime);
        Content.Controls.AddRange([_runtimeNotice.Card, _updateBanner, columns, statsHeader, tiles, _netLine, _netHint]);
        Content.GapBefore[statsHeader] = 20;

        _tips.SetToolTip(_buffer, L.T("电脑这边等着发出去的声音"));
        _tips.SetToolTip(_speakerDelay, L.T("音箱从收到声音到播放需要的时间（音箱自己报告）"));
        _tips.SetToolTip(_effective, L.T("这次连接实际使用的延迟"));
        SetDropoutTip(DropoutTip(0, null));
        _tips.SetToolTip(_resent, L.T("音箱要求重发的包：已重发 / 请求"));
        _tips.SetToolTip(_netStatus, L.T("最近 10 分钟里 Wi-Fi 延迟突增、丢包或音箱要求重发的次数"));

        _tuner.Changed += () =>
        {
            ShowLatency();
            LatencyChanged?.Invoke();
            _app.KickPlayers(); // a scene switch adjusts or restores the players at once
        };
        _statsTimer.Tick += (_, _) => UpdateStats();
        LoadFromConfig();
        ShowUpdate();
        ShowRuntimeNotice();
    }

    // ---------------------------------------------------------------- layout

    private Card BuildSpeakerCard()
    {
        var badge = new GlyphBadge(Glyph.Speaker);
        var title = Ui.Stack(2, _name, _model);
        var head = Ui.Row(14, title, badge, title, _connect);
        _device.AccessibleName = L.T("音箱");
        var picker = Ui.Row(6, _device, _device, _refresh);
        var status = Ui.Row(8, _status, _dot, _status);
        var card = Ui.Card(head, picker, status, _streamStats);
        card.Gap = 12;

        _connect.Click += (_, _) => _app.ToggleConnection();
        _refresh.Click += (_, _) => _app.RefreshDevices();
        _device.SelectionChangeCommitted += (_, _) =>
        {
            if (_device.SelectedIndex >= 0 && _device.SelectedIndex < _devices.Count)
            {
                _app.SelectDevice(_devices[_device.SelectedIndex]);
                ShowSpeaker();
            }
        };
        return card;
    }

    private Card BuildSceneCard()
    {
        _scenes.AccessibleName = L.T("场景");
        var minus = Ui.IconButton(Glyph.Minus, L.T("延迟减 1 ms"));
        var plus = Ui.IconButton(Glyph.Plus, L.T("延迟加 1 ms"));
        minus.TabStop = plus.TabStop = false; // the slider's own keys do this
        _latency.AccessibleName = L.T("延迟");
        var number = Ui.Row(12, _latencyHint, _latencyValue, _latencyHint);
        var slider = Ui.Row(4, _latency, minus, _latency, plus);
        // 管理配置 in the header: in the picker's row it left the picker 「Pr…」 wide in German (the GUI smoke test's screenshots).
        var manage = new FluentButton(L.T("管理配置"), ButtonKind.Subtle);
        var header = _sceneHeader = Ui.Header(L.T("场景"));
        var head = Ui.Row(8, header, header, manage);
        var profiles = Ui.Row(8, _profiles, _profiles, _profileApply);
        _homePodLatency = Ui.Stack(10, _scenes, profiles, number, _lag, slider, _movieHint);
        _receiverLatency = new ReceiverLatencyPanel(_app);
        _receiverLatency.LatencyChanged += () => { ShowLatency(); LatencyChanged?.Invoke(); _app.KickPlayers(); };
        var card = Ui.Card(head, _homePodLatency, _receiverLatency);
        manage.Click += (_, _) => _app.ShowProfiles(FindForm()!);
        _profileApply.Click += (_, _) =>
        {
            if (_profiles.SelectedIndex >= 0 && _profiles.SelectedIndex < _profileIds.Length)
                _app.ApplyUsageProfile(_profileIds[_profiles.SelectedIndex], FindForm());
        };
        _profiles.SelectedIndexChanged += (_, _) => _profileApply.Enabled = _profiles.SelectedIndex >= 0;
        ShowProfiles();
        card.Gap = 10;

        _scenes.SelectionChangeCommitted += (_, _) =>
        {
            if (!_loading && _scenes.SelectedIndex >= 0) _tuner.SelectScene(Scenes.All[_scenes.SelectedIndex]);
        };
        _latency.Scroll += (_, _) => _tuner.UserSet(_latency.Value);
        _latency.Released += (_, _) => _tuner.Released();
        minus.Click += (_, _) => _latency.UserSetValue(_latency.Value - LatencyTuner.SmallStep);
        plus.Click += (_, _) => _latency.UserSetValue(_latency.Value + LatencyTuner.SmallStep);
        return card;
    }

    private Card BuildVolumeCard()
    {
        _volume.AccessibleName = L.T("音量");
        var title = Ui.Header(L.T("音量"));
        var header = Ui.Row(8, title, title, _volumeValue);
        var icon = new GlyphLabel(Glyph.Volume);
        var slider = Ui.Row(10, _volume, icon, _volume);
        var toggles = Ui.Row(28, null, _mute, _night);
        var card = Ui.Card(header, slider, toggles, _capNote);
        card.Gap = 8;

        _tips.SetToolTip(_mute, L.T("让 HomePod 静音，不断开连接"));
        _tips.SetToolTip(_night, L.T("压缩动态范围：爆炸、枪声变小，对白、脚步声变大，适合夜里小音量"));

        _volume.Scroll += (_, _) =>
        {
            _volumeValue.Text = _volume.Value.ToString();
            _app.PreviewVolume(_volume.Value); // the speaker follows the drag; the debounce only saves
            _volumeWait.Restart();
        };
        _volumeWait.Tick += () =>
        {
            _volumeWait.Stop();
            _app.SetVolume(_volume.Value);
            VolumeApplied?.Invoke(_volume.Value);
        };
        _mute.Toggled += (_, _) => _app.SetSpeakerMuted(_mute.Checked);
        _night.Toggled += (_, _) => _app.SetNightMode(_night.Checked);
        return card;
    }

    /// <summary>The caution under the tiles when Wi-Fi jitter eats the latency's margin; collapsed until then.</summary>
    private Card BuildNetworkHint()
    {
        var icon = new GlyphLabel(Glyph.Warning, caution: true);
        var card = Ui.Card(Ui.Row(12, _netHintText, icon, _netHintText, _netApply));
        card.Collapsed = true;
        _netApply.AccessibleName = L.T("应用建议的延迟");
        _tips.SetToolTip(_netApply, L.T("切换到「自定义」场景，用建议的延迟重新连接"));
        _netApply.Click += (_, _) => ApplyNetworkSuggestion();
        return card;
    }

    /// <summary>The new-version banner (an automatic check found one): 更新 / 查看说明 / 跳过此版本; collapsed otherwise.</summary>
    private Card BuildUpdateBanner()
    {
        var icon = new GlyphLabel(Glyph.Download, accent: true);
        var card = Ui.Card(Ui.Row(12, _updateText, icon, _updateText), Ui.Row(8, null, _updateInstall, _updateNotes, _updateSkip));
        card.Collapsed = true;
        _updateInstall.Click += (_, _) =>
        {
            if (_app.UpdateState.Downloading) _app.CancelUpdateDownload();
            else _app.InstallUpdate(FindForm());
        };
        _updateNotes.Click += (_, _) => _app.ShowReleaseNotes();
        _updateSkip.Click += (_, _) => _app.SkipUpdate();
        return card;
    }

    /// <summary>The banner follows the update state (found, downloading, failed); gone once installed, skipped or none.</summary>
    public void ShowUpdate()
    {
        var u = _app.UpdateState;
        _updateBanner.Collapsed = !u.Banner;
        if (!u.Banner) return;
        _updateText.Text = u.Status;
        _updateText.Role = u.Role == TextRole.Secondary ? TextRole.Primary : u.Role;
        _updateInstall.Text = u.Downloading ? L.T("取消") : L.T("更新");
        _updateInstall.Kind = u.Downloading ? ButtonKind.Secondary : ButtonKind.Primary;
        _updateInstall.Enabled = !u.Checking;
        _updateSkip.Enabled = !u.Downloading && !u.Checking;
    }

    /// <summary>The restart notice: shown once the runtime is gone, saying so too while streaming that the sound stops briefly.</summary>
    public void ShowRuntimeNotice() => _runtimeNotice.Show(_app.RuntimeRemoved, _app.Controller.State == StreamState.Streaming);

    /// <summary>The hint's 应用: the only way the network status changes the latency.</summary>
    private void ApplyNetworkSuggestion()
    {
        if (_suggestedMs is not { } ms) return;
        Log.Info($"network hint applied: latency {_app.Controller.EffectiveLatencyMs} → {ms} ms ({Scene.Custom})");
        _appliedSession = _app.Controller.Health.Session;
        _suggestedMs = null;
        _netHint.Collapsed = true;
        _tuner.UseCustom(ms);
    }

    /// <summary>The volume was changed here (the mixer's master slider follows).</summary>
    public event Action<double>? VolumeApplied;

    /// <summary>The scene or the latency shown here changed (the tray flyout follows).</summary>
    public event Action? LatencyChanged;

    /// <summary>The latency shown here: the scene's, or the slider's while it waits to reconnect.</summary>
    public int LatencyMs => _app.ReceiverSelection?.Value ?? _tuner.Value;

    public void FlushLatency() => _tuner.FlushPending();
    public void CancelPendingLatency() => _tuner.ReloadFromConfig();
    public void FlushPendingVolume()
    {
        if (!_volumeWait.Enabled) return;
        _volumeWait.Stop();
        _app.SetVolume(_volume.Value);
    }

    /// <summary>
    /// The profile picker after a profile was applied (here or in the dialog), saved, renamed or deleted: the one applied
    /// last, or the placeholder 「选择配置…」 with 应用 off until one is picked.
    /// </summary>
    public void ShowProfiles()
    {
        var profiles = _app.Config.UsageProfiles;
        _profileIds = profiles.Select(p => p.Id).ToArray();
        _profiles.Items.Clear();
        _profiles.Items.AddRange(profiles.Select(p => p.Name));
        _profiles.SelectedIndex = UsageProfiles.PickerIndex(profiles, _app.Config.LastUsageProfileId);
        _profiles.Enabled = _profileIds.Length > 0;
        _profileApply.Enabled = _profiles.SelectedIndex >= 0;
    }

    public void ReloadConfiguration()
    {
        UpdateFloor();
        _tuner.ReloadFromConfig();
        SetDevices(WithSelected(_devices, _app.Config));
        LoadFromConfig();
        ShowProfiles();
    }

    /// <summary>
    /// The picker's list after the selection changed outside it (a profile): the selected speaker is in it, added (with
    /// the address the config has now) when the last scan didn't list it, so the picker can show it.
    /// </summary>
    internal static List<AirPlayDevice> WithSelected(IReadOnlyList<AirPlayDevice> devices, AppConfig cfg)
    {
        var list = devices.ToList();
        if (cfg.DeviceId != null && SelectedIndex(list, cfg.DeviceId) < 0)
            list.Add(new AirPlayDevice(cfg.DeviceName ?? "?", cfg.DeviceId,
                System.Net.IPAddress.TryParse(cfg.Host, out var address) ? address : System.Net.IPAddress.None,
                7000, "", new Dictionary<string, string>()));
        return list;
    }

    /// <summary>The entry of the selected speaker however its id is written; -1 for none.</summary>
    internal static int SelectedIndex(IReadOnlyList<AirPlayDevice> devices, string? deviceId)
    {
        for (int i = 0; i < devices.Count; i++)
            if (deviceId != null && StreamController.Normalize(devices[i].DeviceId).Equals(StreamController.Normalize(deviceId),
                    StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    // ---------------------------------------------------------------- state

    private void LoadFromConfig()
    {
        _loading = true;
        var cfg = _app.Config;
        UpdateFloor();
        ShowLatency();
        ShowVolume(_app.Controller.Volume ?? cfg.Volume ?? 0);
        if (cfg.Volume is null && _app.Controller.Volume is null) _volumeValue.Text = "--";
        ShowSoundOptions();
        ShowSpeaker();
        _loading = false;
    }

    private void UpdateFloor()
    {
        int floor = _app.Controller.SafeLatency(0);
        _tuner.Floor = floor;
        _latency.Floor = floor;
    }

    /// <summary>Scene, slider, the big number and the lag line.</summary>
    private void ShowLatency()
    {
        bool loading = _loading;
        _loading = true;
        var cfg = _app.Config;
        var selection = _app.ReceiverSelection;
        _homePodLatency.Collapsed = selection != null;
        _sceneHeader.Text = selection == null ? L.T("场景") : L.T("设备延迟（实验性）"); // experimental since 1.1
        _sceneHeader.Wrap = selection != null; // Long receiver labels share the header with 管理配置.
        int ms = selection?.Value ?? _tuner.Value;
        _latency.Value = _tuner.Value;
        _scenes.SelectedIndex = Array.IndexOf(Scenes.All, cfg.Scene);
        _latencyValue.Text = $"{ms} ms";
        _latencyHint.Text = LatencyTuner.Hint(ms);
        _latencyHint.Role = ms < 110 ? TextRole.Critical : TextRole.Secondary;

        var c = _app.Controller;
        bool live = c.State == StreamState.Streaming && (selection != null || (!_tuner.Pending && ms == cfg.LatencyMs));
        int effective = live ? c.EffectiveLatencyMs : selection?.Value ?? c.SafeLatency(ms);
        int captureExtra = c.Capture?.ExtraLatencyMs ?? (_app.Routing.Active ? Audio.RoutedCapture.RoutedExtraLatencyMs : 0);
        _lagMs = LatencyTuner.SoundLagMs(effective, cfg, captureExtra, c.Fifo.TargetMs, _app.CurrentCalibrationContext)
            + (live ? _app.Stems.AddedLatencyMs : 0); // the same as TrayApp.VideoDelayFor
        _lag.Text = L.F("声音比画面晚约 {0} ms", _lagMs);
        _receiverLatency.Refresh(selection, _lag.Text);
        ShowPlayers();
        _loading = loading;
    }

    /// <summary>The 影视 hint: what the local players were set to, and the value for the others.</summary>
    public void ShowPlayers()
    {
        var cfg = _app.Config;
        _movieHint.Collapsed = cfg.Scene != Scene.Movie;
        if (_movieHint.Collapsed) return;
        _movieHint.Text = Players.PlayerText.MovieHint(cfg.MoviePlayerSync, _app.Controller.State == StreamState.Streaming,
            _app.PlayerStatuses, _lagMs);
    }

    /// <summary>Switch scene (tray flyout, hotkey); same rules as the buttons.</summary>
    public void SelectScene(Scene scene) => _tuner.SelectScene(scene);

    /// <summary>Scene, speaker mute, night mode and cap (also after changes from the tray or hotkeys).</summary>
    public void ShowSoundOptions()
    {
        bool loading = _loading;
        _loading = true;
        var cfg = _app.Config;
        ShowLatency();
        _mute.Checked = _app.Controller.Muted;
        _night.Checked = cfg.NightMode;
        int cap = cfg.VolumeCapPercent;
        _volume.Ceiling = cap;
        _volume.Mark = cap < 100 ? cap : null;
        _capNote.Text = cap < 100 ? L.F("音量上限 {0}%（在「设置」里修改）", cap) : "";
        _loading = loading;
    }

    /// <summary>Move the volume slider without sending anything (the value was already applied).</summary>
    public void ShowVolume(double percent)
    {
        if (_volume.IsDragging || _volumeWait.Enabled) return;
        _volume.Value = (int)Math.Round(percent);
        _volumeValue.Text = _volume.Value.ToString();
    }

    public void SetDevices(List<AirPlayDevice> devices)
    {
        _devices = devices;
        _device.Items.Clear();
        var scan = _app.LastScan;
        foreach (var d in devices)
        {
            string label = DeviceLabel(d, scan);
            string kind = ReceiverKinds.Label(d.Kind);
            _device.Items.Add(d.Kind is ReceiverKind.HomePod ? label : $"{label} · {kind}");
        }
        _device.SelectedIndex = SelectedIndex(devices, _app.Config.DeviceId);
        ShowSpeaker();
    }

    public void SetScanning(bool scanning)
    {
        _refresh.Enabled = !scanning;
        _tips.SetToolTip(_refresh, scanning ? L.T("正在搜索音箱…") : L.T("重新搜索音箱"));
    }

    private void ShowSpeaker()
    {
        var cfg = _app.Config;
        _name.Text = cfg.DeviceName ?? L.T("还没有选择音箱");
        var device = _device.SelectedIndex >= 0 && _device.SelectedIndex < _devices.Count ? _devices[_device.SelectedIndex] : null;
        _model.Text = device == null ? (cfg.DeviceId == null ? L.T("在下面选择一台 AirPlay 音箱") : "")
            : string.IsNullOrEmpty(device.Model) ? "" : FriendlyModel(device.Model);
    }

    internal static string FriendlyModel(string model) => SpeakerModels.Name(model);

    /// <summary>
    /// A speaker in the picker: "卧室（HomePod 第二代）"; an Apple TV with home-theater speakers "客厅（Apple TV，家庭影院：卧室）"
    /// (connecting to it streams through the Apple TV, which needs its PIN pairing: only when picked on purpose).
    /// </summary>
    /// <param name="scan">The last scan as found (pairs not merged), where the home theater's speakers are.</param>
    internal static string DeviceLabel(AirPlayDevice d, IReadOnlyList<AirPlayDevice> scan)
    {
        if (string.IsNullOrEmpty(d.Model)) return d.Name;
        if (HomeTheater.IsAppleTv(d) && HomeTheater.SpeakerNames(d, scan) is { } speakers)
            return L.F("{0}（Apple TV，家庭影院：{1}）", d.Name, speakers);
        return L.F("{0}（{1}）", d.Name, FriendlyModel(d.Model));
    }

    public void UpdateState()
    {
        var c = _app.Controller;
        if (c.State == StreamState.Streaming && _lastState != StreamState.Streaming)
        {
            _streamingSince = DateTime.UtcNow;
            _underrunsAtStart = c.Fifo.Underruns;
        }
        _lastState = c.State;

        _dot.DotColor = Icons.For(c.State);
        _status.Text = c.StatusText;
        bool connect = TrayApp.ShowsConnect(c.State);
        _connect.Text = connect ? L.T("连接") : L.T("断开");
        _connect.Kind = connect ? ButtonKind.Primary : ButtonKind.Secondary;
        if (c.State == StreamState.Streaming && c.Volume is { } v) ShowVolume(v);
        _mute.Checked = c.Muted;
        UpdateFloor();
        ShowLatency();
        UpdateStats();
        ShowRuntimeNotice();
    }

    private void UpdateStats()
    {
#if DEBUG
        if (DemoStreaming && _app.Controller.State == StreamState.Idle)
        {
            ShowDemoStreaming();
            return;
        }
#endif
        var c = _app.Controller;
        var s = c.ActiveSender;
        bool streaming = c.AudioSessionIdentity != null && c.State == StreamState.Streaming;
        bool live = streaming && c.HealthWatched && _app.ReceiverSelection == null; // a single speaker: the network is watched
        int arrivalToRender = c.SafeLatency(0) - StreamController.SafetyMarginMs; // the speaker's own time, as the floor assumes it
        var verdict = live
            ? c.Health.Assess(StreamController.HealthClockMs(), c.EffectiveLatencyMs, arrivalToRender, c.RouterWatched, c.PcWired)
            : DemoVerdict(arrivalToRender);
        long pcDropouts = streaming ? c.Fifo.Underruns - _underrunsAtStart : 0;
        int? networkDropouts = live ? verdict!.ConnectionThreats : null;
        string dropouts = DropoutValue(pcDropouts, networkDropouts ?? 0);
        var cfg = _app.Config;
        var source = c.Capture?.DeviceName is { } name ? L.F("音源：{0}", name)
            : cfg.CaptureDeviceId != null ? L.F("音源：{0}", cfg.CaptureDeviceName ?? cfg.CaptureDeviceId)
            : L.T("音源：默认输出设备");
        _streamStats.Role = TextRole.Secondary;
        if (c.Capture?.CaptureDeviceMissing == true)
        {
            // Never the default output instead: that could send voice chat or other private audio to the speaker.
            _streamStats.Role = TextRole.Caution;
            _streamStats.Text = L.F("所选的采集设备「{0}」现在不可用（已拔出、停用或卸载）。为了不把别的声音推到 HomePod，不会改用默认输出；设备恢复后自动继续，也可以在「混音器」里改回跟随 Windows 默认输出。",
                cfg.CaptureDeviceName ?? cfg.CaptureDeviceId);
        }
        else if (c.Capture?.NoOutputDevice == true)
        {
            // No driver/device to play into: apps make no sound at all, so there is nothing to send.
            _streamStats.Role = TextRole.Caution;
            _streamStats.Text = L.T("Windows 没有可用的输出设备，程序的声音无处播放，所以也采集不到。插上耳机或音箱、在声卡设置里关闭插孔检测、用显示器的 HDMI 音频，或安装一个虚拟声卡（如 VB-CABLE），之后会自动恢复。");
        }
        else if (streaming)
        {
            var t = DateTime.UtcNow - _streamingSince;
            string duration = t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
            _streamStats.Text = L.F("已推送 {0} · 断音 {1} 次 · {2}", duration, dropouts, source);
        }
        else
        {
            _streamStats.Text = source;
        }

        _buffer.SetValue(streaming ? $"{c.Fifo.Depth * 1000.0 / RtpSender.SampleRate:F0}" : null);
        // Like the other tiles, only while streaming: the value saved from the last connection still sets the latency
        // floor and the estimates, but shown while disconnected it looked like a live reading.
        _speakerDelay.SetValue(streaming ? (_app.ReceiverSelection != null ? c.ReceiverSession?.ArrivalToRenderMs ?? c.Client?.ArrivalToRenderMs : c.ArrivalToRenderMs)?.ToString() : null);
        _effective.SetValue(streaming ? c.EffectiveLatencyMs.ToString() : null);
        _dropouts.SetValue(streaming ? dropouts : null);
        SetDropoutTip(DropoutTip(pcDropouts, networkDropouts));
        _resent.SetValue(streaming && s != null ? $"{s.Retransmitted}/{s.RetransmitRequests}" : null);
        ShowNetwork(verdict, live);
    }

    /// <summary>
    /// 断音: this connection's dropouts, the ones measured on the PC (the FIFO ran dry) plus the network's likely ones
    /// (<see cref="NetworkVerdict.ConnectionThreats"/>). The speaker never reports a late packet, so on 2026-10-10 the tile
    /// said 0 through stutters heard at 200 and 500 ms; the network's share is an estimate, so the number gets "≈".
    /// </summary>
    internal static string DropoutValue(long pc, int network) => network > 0 ? $"≈{pc + network}" : pc.ToString();

    /// <summary>断音's tooltip: the two shares, or only the PC's while the network is not watched (a group).</summary>
    internal static string DropoutTip(long pc, int? network) => network is { } n
        ? L.F("这次连接里的断音：电脑这边 {0} 次（实测），网络 {1} 次（估计）\n" +
              "Wi-Fi 抖动超出延迟余量时，HomePod 可能断一下音；HomePod 不报告晚到的包，所以网络一项只能估计", pc, n)
        : L.T("这次连接中电脑这边断音的次数");

    private void SetDropoutTip(string tip)
    {
        if (tip == _dropoutTip) return; // every tick: only when it changes
        _dropoutTip = tip;
        _tips.SetToolTip(_dropouts, tip);
    }

    /// <summary>
    /// The 网络 line (jitter episodes in the last 10 minutes) or, when they threaten the margin, the hint naming the
    /// Wi-Fi hop with a suggested latency; for a HomePod taken over from its home-theater Apple TV below
    /// <see cref="HomeTheaterLatencyMs"/>, the home-theater notice right away. Never changes anything by itself; hidden
    /// while not streaming.
    /// </summary>
    /// <param name="v">The current connection's verdict (or the demo's); null while not streaming.</param>
    /// <param name="live">Assessed from a real connection (logged), not the demo.</param>
    private void ShowNetwork(NetworkVerdict? v, bool live)
    {
        var c = _app.Controller;
        string? theaterTv = live ? c.TakenFromAppleTv : null;
        bool applied = c.Health.Session == _appliedSession;
        var notice = NoticeFor(v, theaterTv, applied, c.EffectiveLatencyMs, _app.Config.DeviceName ?? "HomePod");
        bool threatened = notice.Threatened, show = notice.Show;
        _suggestedMs = notice.SuggestedMs;
        _netLine.Collapsed = v == null || show;
        _netHint.Collapsed = !show;
        if (v != null && !show) _netStatus.Text = NetworkLine(v.Episodes);
        if (!show)
        {
            _hintLogged = false;
            return;
        }

        _netHintText.Text = notice.Text!;
        _netApply.Collapsed = _suggestedMs is null;
        if (live && !_hintLogged)
        {
            _hintLogged = true;
            string pc = c.PcNetwork is { } p ? NetworkProfile.KindName(p.Kind) : "unknown";
            Log.Info(threatened
                ? $"network: jitter threatens the {c.EffectiveLatencyMs} ms latency ({v!.Threats} of {v.Episodes} episodes in " +
                  $"10 min beyond its {v.MarginMs} ms margin, hop: {v.Hop}, PC on {pc}); suggesting {_suggestedMs?.ToString() ?? "-"} ms"
                : $"network: taken over from its home-theater Apple TV at {c.EffectiveLatencyMs} ms: their link takes up the Wi-Fi " +
                  $"at regular intervals; suggesting {HomeTheaterLatencyMs} ms or the Apple TV's audio back on the TV");
        }
    }

    /// <summary>What <see cref="ShowNetwork"/> shows instead of the 网络 line (nothing when <see cref="Show"/> is false).</summary>
    /// <param name="Threatened">The jitter threatens the latency's margin (and its suggestion was not applied on this connection).</param>
    /// <param name="HomeTheater">A HomePod taken over from its home-theater Apple TV streams below <see cref="HomeTheaterLatencyMs"/>.</param>
    /// <param name="SuggestedMs">What the hint's 应用 sets (null: no button).</param>
    /// <param name="Text">The hint's text (null when nothing is shown).</param>
    internal sealed record NetworkNotice(bool Threatened, bool HomeTheater, int? SuggestedMs, string? Text)
    {
        public bool Show => Threatened || HomeTheater;
    }

    /// <summary>
    /// The hint's decision. A HomePod taken over from its Apple TV (<paramref name="homeTheaterTv"/>) at a latency the home
    /// theater's link can't leave alone gets the notice at once, before the dropouts add up, suggesting the latency that
    /// absorbs it (not 15 ms more); once dropouts threaten too, the network hint says so with that same suggestion. After 应用
    /// (<paramref name="applied"/>: on this connection) nothing is shown until it reconnects.
    /// </summary>
    internal static NetworkNotice NoticeFor(NetworkVerdict? v, string? homeTheaterTv, bool applied, int effectiveLatencyMs, string speaker)
    {
        bool threatened = v is { Threatened: true } && !applied;
        bool theater = homeTheaterTv != null && !applied && effectiveLatencyMs < HomeTheaterLatencyMs;
        int? suggested = theater ? HomeTheaterLatencyMs : threatened ? v!.SuggestedMs : null;
        string? text = threatened
            ? NetworkHintText(theater ? v! with { SuggestedMs = HomeTheaterLatencyMs } : v!, homeTheaterTv)
            : theater ? HomeTheaterNotice(speaker, homeTheaterTv!) : null;
        return new NetworkNotice(threatened, theater, suggested, text);
    }

    /// <summary>The 网络 line while nothing threatens: 稳定, or how many jitter episodes the last 10 minutes had.</summary>
    internal static string NetworkLine(int episodes) => episodes switch
    {
        0 => L.T("网络：稳定"),
        1 => L.T("网络：最近 10 分钟抖动 1 次"), // its own text, for languages with a singular
        int count => L.F("网络：最近 10 分钟抖动 {0} 次", count),
    };

    /// <summary>
    /// The latency a HomePod taken over from its home-theater Apple TV needs. 2026-10-12 (HomePod 2, Apple TV 4K): with
    /// the home theater on, the two answered pings 60–90 ms late every 2.1 s in step (up to ~145 ms now and then), also
    /// with nothing streaming and whatever our session SETUP said; 105–135 ms stuttered, 500 ms played clean. 200 ms
    /// leaves 110 ms for it (not yet heard on the device).
    /// </summary>
    internal const int HomeTheaterLatencyMs = NetworkHealth.MaxSuggestedMs;

    /// <summary>Shown as soon as such a HomePod streams below <see cref="HomeTheaterLatencyMs"/>, before any dropout.</summary>
    internal static string HomeTheaterNotice(string speaker, string appleTv) =>
        L.F("{0}是 Apple TV「{1}」的家庭影院音箱：它们之间的连接会定时占用 Wi-Fi，延迟低于 {2} ms 时可能断音。" +
            "打游戏时在 Apple TV 上把音频输出改回电视最管用，也可以把延迟调到 {2} ms", speaker, appleTv, HomeTheaterLatencyMs)
            .Replace(" ms", " ms");

    /// <summary>
    /// The hint for a threatened verdict. On the HomePod's hop (always, with a wired PC) it leads with the band: on
    /// 2026-10-10 the HomePod sat 50 cm from the router on a crowded 2.4 GHz band (the router's one name for both bands
    /// put it there): ping median 21 ms, 4% lost, stutters even at 500 ms, and moving it changed nothing; on 5 GHz, 2 ms
    /// and nothing lost. The app can't see the band, hence "if".
    /// </summary>
    /// <param name="homeTheaterTv">The Apple TV the HomePod was taken over from (<see cref="StreamController.TakenFromAppleTv"/>):
    /// on the HomePod's hop that, not the band, is the advice. 2026-10-12: with the HomePod taken over from its Apple TV,
    /// both answered pings 60–90 ms late every 2.1 s, in step, and 120 ms stuttered; with the Apple TV's audio output set
    /// back to the TV, the Apple TV went clean at once and the stutters stopped.</param>
    internal static string NetworkHintText(NetworkVerdict v, string? homeTheaterTv = null)
    {
        int n = v.Threats; // not every episode: 2026-10-12 it said 38, every spike over 30 ms, most of them harmless
        bool homeTheater = homeTheaterTv != null && v.Hop != NetworkHop.Pc;
        return ((v.Hop, v.SuggestedMs) switch
        {
            (_, { } ms) when homeTheater => L.F("HomePod 那边的 Wi-Fi 最近不太稳（10 分钟内可能断音 {0} 次），建议把延迟调到 {1} ms。" +
                                                "这台 HomePod 是 Apple TV「{2}」的家庭影院音箱，它们之间的连接会定时占用 Wi-Fi，" +
                                                "在 Apple TV 上把音频输出改回电视更管用", n, ms, homeTheaterTv),
            (_, null) when homeTheater => L.F("HomePod 那边的 Wi-Fi 最近不太稳（10 分钟内可能断音 {0} 次）。这台 HomePod 是 Apple TV「{1}」" +
                                              "的家庭影院音箱，它们之间的连接会定时占用 Wi-Fi：在 Apple TV 上把音频输出改回电视最管用", n, homeTheaterTv),
            (NetworkHop.Speaker, { } ms) => L.F("HomePod 那边的 Wi-Fi 最近不太稳（10 分钟内可能断音 {0} 次），建议把延迟调到 {1} ms。" +
                                                 "HomePod 如果连在 2.4G 上，改连 5G 更管用", n, ms),
            (NetworkHop.Pc, { } ms) => L.F("电脑这边的 Wi-Fi 最近不太稳（10 分钟内可能断音 {0} 次），建议把延迟调到 {1} ms", n, ms),
            (_, { } ms) => L.F("Wi-Fi 最近不太稳（10 分钟内可能断音 {0} 次），建议把延迟调到 {1} ms。HomePod 如果连在 2.4G 上，改连 5G 更管用", n, ms),
            (NetworkHop.Speaker, null) => L.F("HomePod 那边的 Wi-Fi 最近不太稳（10 分钟内可能断音 {0} 次）。延迟已经不低了：" +
                                              "HomePod 如果连在 2.4G 上（路由器开了双频合一时常见），改连 5G 最管用，其次才是摆放位置", n),
            (NetworkHop.Pc, null) => L.F("电脑这边的 Wi-Fi 最近不太稳（10 分钟内可能断音 {0} 次）。延迟已经不低了，试试关掉无线网卡的节能，或改用网线", n),
            (_, null) => L.F("Wi-Fi 最近不太稳（10 分钟内可能断音 {0} 次）。延迟已经不低了：HomePod 如果连在 2.4G 上，改连 5G 最管用，其次再改善 Wi-Fi 信号", n),
        }).Replace(" ms", "\u00A0ms").Replace(" GHz", "\u00A0GHz"); // "135 ms" and "5 GHz" stay on one line
    }

#if DEBUG
    private static readonly string? DemoNetwork = Environment.GetEnvironmentVariable("HOMEPODCAST_DEMO_NETHINT") is { Length: > 0 } d ? d : null;

    /// <summary>
    /// Debug builds with HOMEPODCAST_DEMO_NETHINT set (for screenshots): the status as if the HomePod's Wi-Fi had spiked
    /// three times in 10 minutes (the hint with the band advice), or with "stable", as if nothing happened. While not
    /// streaming, or with HOMEPODCAST_DEMO_STREAMING (then 断音 counts the three as the network's: ≈3). Release builds
    /// have no such path.
    /// </summary>
    private NetworkVerdict? DemoVerdict(int arrivalToRender)
    {
        if (DemoNetwork == null) return null;
        var demo = new NetworkHealth();
        if (DemoNetwork != "stable")
        {
            foreach (long at in new long[] { 60_000, 250_000, 480_000 }) demo.AddPing(PingTarget.Speaker, at, 75);
            demo.AddPing(PingTarget.Router, 150_000, 34); // the router alone: not jitter
        }
        return demo.Assess(500_000, _app.Controller.SafeLatency(_app.Config.LatencyMs), arrivalToRender, routerWatched: true);
    }

    private static readonly bool DemoStreaming = Environment.GetEnvironmentVariable("HOMEPODCAST_DEMO_STREAMING") is { Length: > 0 };
    private static readonly DateTime DemoSince = DateTime.UtcNow.AddMinutes(-12).AddSeconds(-34);

    /// <summary>
    /// Debug builds with HOMEPODCAST_DEMO_STREAMING set, while idle (tutorial and promo recordings without a speaker):
    /// the page as it looks while streaming, with the values measured on the real machine on 2026-10-08 (85 ms in the
    /// speaker, a 16 ms buffer, no dropouts, no resends), and the network of HOMEPODCAST_DEMO_NETHINT if set. Release
    /// builds have no such path.
    /// </summary>
    private void ShowDemoStreaming()
    {
        var cfg = _app.Config;
        var t = DateTime.UtcNow - DemoSince;
        var v = DemoVerdict(_app.Controller.SafeLatency(0) - StreamController.SafetyMarginMs);
        int network = v?.ConnectionThreats ?? 0;
        string dropouts = DropoutValue(0, network);
        _dot.DotColor = Icons.Streaming;
        _status.Text = L.F("已连接 · {0}", cfg.DeviceName ?? "HomePod");
        _connect.Text = L.T("断开");
        _connect.Kind = ButtonKind.Secondary;
        if (_volumeValue.Text == "--") ShowVolume(cfg.Volume ?? 30);
        _streamStats.Role = TextRole.Secondary;
        _streamStats.Text = L.F("已推送 {0} · 断音 {1} 次 · {2}", $"{t.Minutes}:{t.Seconds:00}", dropouts, L.T("音源：默认输出设备"));
        _effective.SetValue(_app.Controller.SafeLatency(cfg.LatencyMs).ToString());
        _buffer.SetValue((15 + t.Seconds % 3).ToString()); // the FIFO's depth wanders around its 16 ms target
        _speakerDelay.SetValue("85");
        _dropouts.SetValue(dropouts);
        SetDropoutTip(DropoutTip(0, network));
        _resent.SetValue("0/0");
        if (v != null)
        {
            ShowNetwork(v, live: false);
            return;
        }
        _netHint.Collapsed = true;
        _netLine.Collapsed = false;
        _netStatus.Text = L.T("网络：稳定");
    }
#else
    private static NetworkVerdict? DemoVerdict(int arrivalToRender) => null;
#endif

    public override void PageShown()
    {
        UpdateState();
        _statsTimer.Start();
    }

    public override void PageHidden() => _statsTimer.Stop();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _statsTimer.Dispose();
            _sliderWait.Dispose();
            _sceneWait.Dispose();
            _volumeWait.Dispose();
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>A glyph that only decorates (the volume icon next to the slider, the network hint's warning, the update banner's).</summary>
    internal sealed class GlyphLabel(string glyph, bool caution = false, bool accent = false) : FluentControl
    {
        public override Size GetPreferredSize(Size proposedSize) => new(Dp(20), Dp(20));

        protected override void OnPaint(PaintEventArgs e) =>
            Shapes.Glyph(e.Graphics, glyph, Theme.IconFont(16, DeviceDpi), ClientRectangle,
                caution ? P.Caution : accent ? P.AccentText : P.TextSecondary);
    }
}
