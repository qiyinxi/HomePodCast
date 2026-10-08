using HomePodCast.Net;
using HomePodCast.UI.Controls;

namespace HomePodCast.UI.Pages;

/// <summary>
/// 首页: which speaker, what latency now, is anything wrong. Speaker card (picker, state, connect), scene
/// card (scene, latency, how far the sound lags the picture), volume card (volume, mute, night mode), and
/// tiles with the stream statistics the app actually measures.
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
    private readonly TextBlock _latencyHint = new("", TextStyle.Caption, TextRole.Secondary);
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
        Content.Controls.AddRange([columns, statsHeader, tiles]);
        Content.GapBefore[statsHeader] = 20;

        _tips.SetToolTip(_buffer, L.T("电脑这边等着发出去的声音"));
        _tips.SetToolTip(_speakerDelay, L.T("音箱从收到声音到播放需要的时间（音箱自己报告）"));
        _tips.SetToolTip(_effective, L.T("这次连接实际使用的延迟"));
        _tips.SetToolTip(_dropouts, L.T("这次连接中电脑这边断音的次数"));
        _tips.SetToolTip(_resent, L.T("音箱要求重发的包：已重发 / 请求"));

        _tuner.Changed += () =>
        {
            ShowLatency();
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

    /// <summary>The volume was changed here (the mixer's master slider follows).</summary>
    public event Action<double>? VolumeApplied;

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
        _lagMs = LatencyTuner.SoundLagMs(effective, cfg, captureExtra);
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

    /// <summary>Switch scene (tray menu, hotkey); same rules as the buttons.</summary>
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
        var c = _app.Controller;
        var s = c.ActiveSender;
        bool streaming = s != null && c.State == StreamState.Streaming;
        var source = c.Capture?.DeviceName is { } name ? L.F("音源：{0}", name) : L.T("音源：默认输出设备");
        if (streaming)
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
    }

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

    /// <summary>A glyph that only decorates (the volume icon next to the slider).</summary>
    private sealed class GlyphLabel(string glyph) : FluentControl
    {
        public override Size GetPreferredSize(Size proposedSize) => new(Dp(20), Dp(20));

        protected override void OnPaint(PaintEventArgs e) =>
            Shapes.Glyph(e.Graphics, glyph, Theme.IconFont(16, DeviceDpi), ClientRectangle, P.TextSecondary);
    }
}
