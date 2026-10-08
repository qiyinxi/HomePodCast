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
    private readonly TextBlock _name = new("", TextStyle.Subtitle);
    private readonly TextBlock _model = new("", TextStyle.Caption, TextRole.Secondary);
    private readonly FluentButton _connect = new(L.T("连接"), ButtonKind.Primary) { MinWidth = 96 };
    private readonly FluentComboBox _device = new();
    private readonly FluentButton _refresh;
    private readonly StatusDot _dot = new();
    private readonly TextBlock _status = new("", TextStyle.Body, wrap: true);
    private readonly TextBlock _streamStats = Ui.Note();
    private List<AirPlayDevice> _devices = [];

    // scene / latency
    private readonly Segmented _scenes = new(Scenes.All.Select(Scenes.Name).ToArray()) { EqualWidths = false }; // "Recommended" is long
    private readonly TextBlock _latencyValue = new("", TextStyle.Display);
    private readonly TextBlock _latencyHint = new("", TextStyle.Caption, TextRole.Secondary, wrap: true); // "极限：…" is long
    private readonly TextBlock _lag = new("", TextStyle.Body, TextRole.Secondary, wrap: true);
    private readonly FluentSlider _latency = Ui.Slider(0, LatencyTuner.MaxMs, LatencyTuner.SmallStep, LatencyTuner.LargeStep);
    private readonly TextBlock _movieHint = Ui.Note();
    private int _lagMs;
    private readonly LatencyTuner _tuner;
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
        var statsHeader = Ui.Section(L.T("推流状态"));
        _netLine = Ui.Row(8, _netStatus, new GlyphLabel(Glyph.Wifi), _netStatus);
        _netLine.Collapsed = true;
        _netHint = BuildNetworkHint();
        Content.Controls.AddRange([columns, statsHeader, tiles, _netLine, _netHint]);
        Content.GapBefore[statsHeader] = 20;

        _tips.SetToolTip(_buffer, L.T("电脑这边等着发出去的声音"));
        _tips.SetToolTip(_speakerDelay, L.T("音箱从收到声音到播放需要的时间（音箱自己报告）"));
        _tips.SetToolTip(_effective, L.T("这次连接实际使用的延迟"));
        _tips.SetToolTip(_dropouts, L.T("这次连接中电脑这边断音的次数"));
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
        var card = Ui.Card(Ui.Header(L.T("场景")), _scenes, number, _lag, slider, _movieHint);
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
    public int LatencyMs => _tuner.Value;

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
        int ms = _tuner.Value;
        _latency.Value = ms;
        _scenes.SelectedIndex = Array.IndexOf(Scenes.All, cfg.Scene);
        _latencyValue.Text = $"{ms} ms";
        _latencyHint.Text = LatencyTuner.Hint(ms);
        _latencyHint.Role = ms < 110 ? TextRole.Critical : TextRole.Secondary;

        var c = _app.Controller;
        bool live = c.State == StreamState.Streaming && !_tuner.Pending && ms == cfg.LatencyMs;
        int effective = live ? c.EffectiveLatencyMs : c.SafeLatency(ms);
        int captureExtra = c.Capture?.ExtraLatencyMs ?? (_app.Routing.Active ? Audio.RoutedCapture.RoutedExtraLatencyMs : 0);
        _lagMs = LatencyTuner.SoundLagMs(effective, cfg, captureExtra, c.Fifo.TargetMs);
        _lag.Text = L.F("声音比画面晚约 {0} ms", _lagMs);
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
        foreach (var d in devices)
            _device.Items.Add(string.IsNullOrEmpty(d.Model) ? d.Name : L.F("{0}（{1}）", d.Name, FriendlyModel(d.Model)));
        var cfgId = _app.Config.DeviceId;
        _device.SelectedIndex = devices.FindIndex(d =>
            cfgId != null && StreamController.Normalize(d.DeviceId).Equals(StreamController.Normalize(cfgId), StringComparison.OrdinalIgnoreCase));
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

    internal static string FriendlyModel(string model) => model switch
    {
        _ when model.StartsWith("AudioAccessory6") => L.T("HomePod 第二代"),
        _ when model.StartsWith("AudioAccessory5") => "HomePod mini",
        _ when model.StartsWith("AudioAccessory1") => "HomePod",
        _ when model.StartsWith("AppleTV") => "Apple TV",
        _ => model,
    };

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
        bool idle = c.State == StreamState.Idle;
        _connect.Text = idle ? L.T("连接") : L.T("断开");
        _connect.Kind = idle ? ButtonKind.Primary : ButtonKind.Secondary;
        if (c.State == StreamState.Streaming && c.Volume is { } v) ShowVolume(v);
        _mute.Checked = c.Muted;
        UpdateFloor();
        ShowLatency();
        UpdateStats();
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
        bool streaming = s != null && c.State == StreamState.Streaming;
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
            _streamStats.Text = L.F("已推送 {0} · 断音 {1} 次 · {2}", duration, c.Fifo.Underruns - _underrunsAtStart, source);
        }
        else
        {
            _streamStats.Text = source;
        }

        _buffer.SetValue(streaming ? $"{c.Fifo.Depth * 1000.0 / RtpSender.SampleRate:F0}" : null);
        _speakerDelay.SetValue(c.ArrivalToRenderMs?.ToString());
        _effective.SetValue(streaming ? c.EffectiveLatencyMs.ToString() : null);
        _dropouts.SetValue(streaming ? (c.Fifo.Underruns - _underrunsAtStart).ToString() : null);
        _resent.SetValue(streaming ? $"{s!.Retransmitted}/{s.RetransmitRequests}" : null);
        ShowNetwork(streaming);
    }

    /// <summary>
    /// The 网络 line (jitter episodes in the last 10 minutes) or, when they threaten the margin, the hint naming the
    /// Wi-Fi hop with a suggested latency. Never changes anything by itself; hidden while not streaming.
    /// </summary>
    private void ShowNetwork(bool streaming)
    {
        var c = _app.Controller;
        bool live = streaming && c.HealthWatched;
        int arrivalToRender = c.SafeLatency(0) - StreamController.SafetyMarginMs; // the speaker's own time, as the floor assumes it
        var v = live ? c.Health.Assess(StreamController.HealthClockMs(), c.EffectiveLatencyMs, arrivalToRender, c.RouterWatched)
            : DemoVerdict(arrivalToRender);

        bool show = v is { Threatened: true } && c.Health.Session != _appliedSession;
        _suggestedMs = show ? v!.SuggestedMs : null;
        _netLine.Collapsed = v == null || show;
        _netHint.Collapsed = !show;
        if (v != null && !show)
            _netStatus.Text = v.Episodes switch
            {
                0 => L.T("网络：稳定"),
                1 => L.T("网络：最近 10 分钟抖动 1 次"), // its own text, for languages with a singular
                int count => L.F("网络：最近 10 分钟抖动 {0} 次", count),
            };
        if (!show)
        {
            _hintLogged = false;
            return;
        }

        int n = v!.Episodes;
        _netHintText.Text = ((v.Hop, v.SuggestedMs) switch
        {
            (NetworkHop.Speaker, { } ms) => L.F("HomePod 那边的 Wi-Fi 最近不太稳（10 分钟内 {0} 次），建议把延迟调到 {1} ms", n, ms),
            (NetworkHop.Pc, { } ms) => L.F("电脑这边的 Wi-Fi 最近不太稳（10 分钟内 {0} 次），建议把延迟调到 {1} ms", n, ms),
            (_, { } ms) => L.F("Wi-Fi 最近不太稳（10 分钟内 {0} 次），建议把延迟调到 {1} ms", n, ms),
            (NetworkHop.Speaker, null) => L.F("HomePod 那边的 Wi-Fi 最近不太稳（10 分钟内 {0} 次）。延迟已经不低了，试试把 HomePod 放到能看见路由器的地方", n),
            (NetworkHop.Pc, null) => L.F("电脑这边的 Wi-Fi 最近不太稳（10 分钟内 {0} 次）。延迟已经不低了，试试关掉无线网卡的节能，或改用网线", n),
            (_, null) => L.F("Wi-Fi 最近不太稳（10 分钟内 {0} 次）。延迟已经不低了，试试改善 Wi-Fi 信号", n),
        }).Replace(" ms", "\u00A0ms"); // "135 ms" stays on one line
        _netApply.Collapsed = v.SuggestedMs is null;
        if (live && !_hintLogged)
        {
            _hintLogged = true;
            Log.Info($"network: jitter threatens the {c.EffectiveLatencyMs} ms latency ({v.Threats} of {n} episodes in 10 min " +
                     $"beyond its {v.MarginMs} ms margin, hop: {v.Hop}); suggesting {v.SuggestedMs?.ToString() ?? "-"} ms");
        }
    }

#if DEBUG
    private static readonly string? DemoNetwork = Environment.GetEnvironmentVariable("HOMEPODCAST_DEMO_NETHINT") is { Length: > 0 } d ? d : null;

    /// <summary>
    /// Debug builds with HOMEPODCAST_DEMO_NETHINT set, while not streaming (for screenshots): the status as if the
    /// HomePod's Wi-Fi had spiked three times in 10 minutes, or with "stable", as if nothing happened. Release builds
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
    /// speaker, a 16 ms buffer, no dropouts, no resends). Release builds have no such path.
    /// </summary>
    private void ShowDemoStreaming()
    {
        var cfg = _app.Config;
        var t = DateTime.UtcNow - DemoSince;
        _dot.DotColor = Icons.Streaming;
        _status.Text = L.F("已连接 · {0}", cfg.DeviceName ?? "HomePod");
        _connect.Text = L.T("断开");
        _connect.Kind = ButtonKind.Secondary;
        if (_volumeValue.Text == "--") ShowVolume(cfg.Volume ?? 30);
        _streamStats.Role = TextRole.Secondary;
        _streamStats.Text = L.F("已推送 {0} · 断音 {1} 次 · {2}", $"{t.Minutes}:{t.Seconds:00}", 0, L.T("音源：默认输出设备"));
        _effective.SetValue(_app.Controller.SafeLatency(cfg.LatencyMs).ToString());
        _buffer.SetValue((15 + t.Seconds % 3).ToString()); // the FIFO's depth wanders around its 16 ms target
        _speakerDelay.SetValue("85");
        _dropouts.SetValue("0");
        _resent.SetValue("0/0");
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

    /// <summary>A glyph that only decorates (the volume icon next to the slider, the network hint's warning).</summary>
    private sealed class GlyphLabel(string glyph, bool caution = false) : FluentControl
    {
        public override Size GetPreferredSize(Size proposedSize) => new(Dp(20), Dp(20));

        protected override void OnPaint(PaintEventArgs e) =>
            Shapes.Glyph(e.Graphics, glyph, Theme.IconFont(16, DeviceDpi), ClientRectangle, caution ? P.Caution : P.TextSecondary);
    }
}
